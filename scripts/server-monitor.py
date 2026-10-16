#!/usr/bin/env python3
import argparse
import datetime
import http.client
import json
import pathlib
import socket
import ssl
import time


def emit(event, **fields):
    print(json.dumps(dict(event=event, time=datetime.datetime.now(datetime.timezone.utc).isoformat(), **fields)), flush=True)


def counters():
    result = {}
    for line in pathlib.Path('/proc/net/dev').read_text().splitlines()[2:]:
        interface, values = line.split(':')
        if interface.strip() == 'lo':
            continue
        values = list(map(int, values.split()))
        for key, index in [('rx_bytes', 0), ('rx_packets', 1), ('rx_errors', 2), ('rx_drops', 3), ('tx_bytes', 8), ('tx_packets', 9), ('tx_errors', 10), ('tx_drops', 11)]:
            result[key] = result.get(key, 0) + values[index]
    wanted = {'ListenOverflows', 'ListenDrops', 'SyncookiesSent', 'TCPReqQFullDoCookies', 'TCPReqQFullDrop', 'RetransSegs', 'AttemptFails', 'InErrors', 'RcvbufErrors', 'SndbufErrors', 'NoPorts'}
    for path in ['/proc/net/netstat', '/proc/net/snmp']:
        rows = pathlib.Path(path).read_text().splitlines()
        for header, values in zip(rows[::2], rows[1::2]):
            for key, value in zip(header.split()[1:], values.split()[1:]):
                if key in wanted:
                    result[header.split(':')[0] + '_' + key] = int(value)
    return result


def state():
    result = {'load1': float(pathlib.Path('/proc/loadavg').read_text().split()[0])}
    for line in pathlib.Path('/proc/meminfo').read_text().splitlines():
        key, value = line.split(':')
        if key in ('MemAvailable', 'MemTotal'):
            result[key + '_bytes'] = int(value.split()[0]) * 1024
    result.update(tcp_established=0, tcp_syn_recv=0)
    for file in ['/proc/net/tcp', '/proc/net/tcp6']:
        path = pathlib.Path(file)
        if not path.exists():
            continue
        for row in path.read_text().splitlines()[1:]:
            status = row.split()[3]
            if status == '01':
                result['tcp_established'] += 1
            elif status == '03':
                result['tcp_syn_recv'] += 1
    return result


def health(domain, port):
    started = time.monotonic()
    conn = http.client.HTTPSConnection(domain, port, timeout=3, context=ssl.create_default_context())
    conn._create_connection = lambda address, timeout, *args, **kwargs: socket.create_connection(('127.0.0.1', port), timeout)
    try:
        conn.request('GET', '/healthz')
        response = conn.getresponse()
        healthy = response.status == 200 and response.read(16) == b'ok'
        return {'healthy': healthy, 'status': response.status, 'latency_ms': round((time.monotonic() - started) * 1000)}
    except (OSError, http.client.HTTPException) as error:
        return {'healthy': False, 'error': type(error).__name__}
    finally:
        conn.close()


def anomalies(delta, current, elapsed):
    reasons = []
    if delta.get('rx_packets', 0) / elapsed > 25000:
        reasons.append('packet_rate')
    if delta.get('rx_bytes', 0) * 8 / elapsed > 100_000_000:
        reasons.append('bandwidth')
    if current['tcp_syn_recv'] >= 128:
        reasons.append('syn_backlog')
    for key in ['TcpExt_ListenOverflows', 'TcpExt_ListenDrops', 'TcpExt_SyncookiesSent', 'TcpExt_TCPReqQFullDrop', 'Udp_RcvbufErrors', 'rx_drops']:
        if delta.get(key, 0) > 0:
            reasons.append(key)
    return reasons


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--domain', required=True)
    parser.add_argument('--port', type=int, default=443)
    parser.add_argument('--once', action='store_true')
    args = parser.parse_args()
    previous, previous_time, previous_health = None, time.monotonic(), None
    while True:
        try:
            now = time.monotonic()
            sample, current = counters(), state()
            elapsed = max(now - previous_time, 0.001)
            delta = {key: max(0, value - previous.get(key, value)) for key, value in sample.items()} if previous else {}
            probe = health(args.domain, args.port)
            emit('host_summary', interval_seconds=round(elapsed, 3) if previous else 0, delta=delta, gauges=current, local_https=probe)
            if probe['healthy'] != previous_health:
                emit('health_state', **probe)
            reasons = anomalies(delta, current, elapsed) if previous else []
            if reasons:
                emit('traffic_anomaly', reasons=reasons)
            previous, previous_time, previous_health = sample, now, probe['healthy']
        except (OSError, ValueError, IndexError) as error:
            emit('monitor_error', error=type(error).__name__)
        if args.once:
            return
        time.sleep(max(1, 15 - (time.monotonic() - now)))


if __name__ == '__main__':
    main()
