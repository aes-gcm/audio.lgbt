#pragma once
#include <dxgi1_2.h>
#include <map>
#include <string>

// Monitor capture deliberately never creates a Windows.Graphics.Capture session.
// Fullscreen games can reset WGC's border policy; Desktop Duplication has no border.
class DesktopCapture {
    winrt::com_ptr<ID3D11Device> device;
    winrt::com_ptr<ID3D11DeviceContext> context;
    winrt::com_ptr<ID3D11Texture2D> staging;
    winrt::com_ptr<IDXGIOutputDuplication> duplication;
    std::wstring display;
    DXGI_MODE_ROTATION rotation = DXGI_MODE_ROTATION_IDENTITY;
    DXGI_OUTDUPL_POINTER_POSITION pointer{};
    DXGI_OUTDUPL_POINTER_SHAPE_INFO shape{};
    std::vector<BYTE> pointerBytes, pixels;
    std::mutex gate;
    UINT textureWidth = 0, textureHeight = 0;
    int width = 0, height = 0;
    uint64_t revision = 0, retryAt = 0;

    void Reset() {
        duplication = nullptr; staging = nullptr; context = nullptr; device = nullptr;
        pointer = {}; shape = {}; pointerBytes.clear(); textureWidth = textureHeight = 0;
        pixels.clear(); width = height = 0;
        retryAt = GetTickCount64() + 150;
    }
    static bool Transition(HRESULT hr) {
        return hr == DXGI_ERROR_ACCESS_LOST || hr == DXGI_ERROR_DEVICE_REMOVED || hr == DXGI_ERROR_DEVICE_RESET ||
            hr == DXGI_ERROR_NOT_CURRENTLY_AVAILABLE || hr == DXGI_ERROR_SESSION_DISCONNECTED || hr == DXGI_ERROR_UNSUPPORTED ||
            hr == DXGI_ERROR_NOT_FOUND || hr == E_ACCESSDENIED;
    }
    HRESULT Initialize() {
        winrt::com_ptr<IDXGIFactory1> factory;
        winrt::check_hresult(CreateDXGIFactory1(__uuidof(IDXGIFactory1), factory.put_void()));
        for (UINT a = 0; ; ++a) {
            winrt::com_ptr<IDXGIAdapter1> adapter;
            auto hr = factory->EnumAdapters1(a, adapter.put()); if (hr == DXGI_ERROR_NOT_FOUND) break; winrt::check_hresult(hr);
            for (UINT o = 0; ; ++o) {
                winrt::com_ptr<IDXGIOutput> output;
                hr = adapter->EnumOutputs(o, output.put()); if (hr == DXGI_ERROR_NOT_FOUND) break; winrt::check_hresult(hr);
                DXGI_OUTPUT_DESC desc{}; winrt::check_hresult(output->GetDesc(&desc));
                if (display != desc.DeviceName || !desc.AttachedToDesktop) continue;
                winrt::check_hresult(D3D11CreateDevice(adapter.get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    nullptr, 0, D3D11_SDK_VERSION, device.put(), nullptr, context.put()));
                hr = output.as<IDXGIOutput1>()->DuplicateOutput(device.get(), duplication.put());
                if (FAILED(hr)) { Reset(); return hr; }
                DXGI_OUTDUPL_DESC capture{}; duplication->GetDesc(&capture); rotation = capture.Rotation;
                return S_OK;
            }
        }
        return DXGI_ERROR_NOT_FOUND;
    }
    void DrawPointer() {
        if (!pointer.Visible || pointerBytes.empty()) return;
        const int pw = static_cast<int>(shape.Width);
        const int ph = static_cast<int>(shape.Height / (shape.Type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME ? 2 : 1));
        if (pw <= 0 || ph <= 0 || static_cast<uint64_t>(shape.Pitch) * shape.Height > pointerBytes.size()) return;
        for (int y = 0; y < ph; ++y) {
            int dy = pointer.Position.y + y; if (dy < 0 || dy >= height) continue;
            for (int x = 0; x < pw; ++x) {
                int dx = pointer.Position.x + x; if (dx < 0 || dx >= width) continue;
                BYTE* dest = pixels.data() + (static_cast<size_t>(dy) * width + dx) * 4;
                if (shape.Type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MONOCHROME) {
                    if (static_cast<UINT>(x / 8) >= shape.Pitch) continue;
                    BYTE mask = static_cast<BYTE>(0x80 >> (x % 8));
                    bool andBit = (pointerBytes[static_cast<size_t>(y) * shape.Pitch + x / 8] & mask) != 0;
                    bool xorBit = (pointerBytes[static_cast<size_t>(y + ph) * shape.Pitch + x / 8] & mask) != 0;
                    for (int c = 0; c < 3; ++c) dest[c] = (dest[c] & (andBit ? 255 : 0)) ^ (xorBit ? 255 : 0);
                } else {
                    if (static_cast<uint64_t>(x + 1) * 4 > shape.Pitch) continue;
                    const BYTE* src = pointerBytes.data() + static_cast<size_t>(y) * shape.Pitch + x * 4;
                    if (shape.Type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE_MASKED_COLOR) {
                        for (int c = 0; c < 3; ++c) dest[c] = src[3] ? dest[c] ^ src[c] : src[c];
                    } else if (shape.Type == DXGI_OUTDUPL_POINTER_SHAPE_TYPE_COLOR) {
                        for (int c = 0; c < 3; ++c) dest[c] = static_cast<BYTE>((src[c] * src[3] + dest[c] * (255 - src[3]) + 127) / 255);
                    }
                }
                dest[3] = 255;
            }
        }
    }
    HRESULT ReadFrame() {
        if (!duplication) {
            if (GetTickCount64() < retryAt) return S_FALSE;
            auto hr = Initialize();
            if (FAILED(hr)) { Reset(); return Transition(hr) ? S_FALSE : hr; }
        }
        DXGI_OUTDUPL_FRAME_INFO info{}; winrt::com_ptr<IDXGIResource> resource;
        auto hr = duplication->AcquireNextFrame(0, &info, resource.put());
        if (hr == DXGI_ERROR_WAIT_TIMEOUT) return S_FALSE;
        if (FAILED(hr)) { Reset(); return Transition(hr) ? S_FALSE : hr; }
        // Release the acquired DXGI frame even if allocation, mapping or copying fails.
        auto acquired = duplication;
        struct Release { IDXGIOutputDuplication* output; ~Release() { output->ReleaseFrame(); } } release{acquired.get()};
        auto texture = resource.as<ID3D11Texture2D>(); D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
        if (desc.Width == 0 || desc.Height == 0 || static_cast<uint64_t>(desc.Width) * desc.Height > 4096ULL * 4096) return E_INVALIDARG;
        if (!staging || textureWidth != desc.Width || textureHeight != desc.Height) {
            staging = nullptr; textureWidth = desc.Width; textureHeight = desc.Height;
            auto copy = desc; copy.Usage = D3D11_USAGE_STAGING; copy.BindFlags = 0; copy.CPUAccessFlags = D3D11_CPU_ACCESS_READ; copy.MiscFlags = 0;
            winrt::check_hresult(device->CreateTexture2D(&copy, nullptr, staging.put()));
        }
        const bool rotated = rotation == DXGI_MODE_ROTATION_ROTATE90 || rotation == DXGI_MODE_ROTATION_ROTATE270;
        width = rotated ? desc.Height : desc.Width; height = rotated ? desc.Width : desc.Height;
        pixels.resize(static_cast<size_t>(width) * height * 4);
        context->CopyResource(staging.get(), texture.get());
        D3D11_MAPPED_SUBRESOURCE mapped{}; hr = context->Map(staging.get(), 0, D3D11_MAP_READ, 0, &mapped);
        if (FAILED(hr)) { Reset(); return Transition(hr) ? S_FALSE : hr; }
        for (UINT y = 0; y < desc.Height; ++y) {
            auto row = reinterpret_cast<const uint32_t*>(static_cast<const BYTE*>(mapped.pData) + size_t(y) * mapped.RowPitch);
            if (rotation == DXGI_MODE_ROTATION_IDENTITY || rotation == DXGI_MODE_ROTATION_UNSPECIFIED) {
                memcpy(pixels.data() + size_t(y) * width * 4, row, size_t(width) * 4); continue;
            }
            auto dest = reinterpret_cast<uint32_t*>(pixels.data());
            for (UINT x = 0; x < desc.Width; ++x) {
                if (rotation == DXGI_MODE_ROTATION_ROTATE90) dest[size_t(x) * width + width - 1 - y] = row[x];
                else if (rotation == DXGI_MODE_ROTATION_ROTATE180) dest[size_t(height - 1 - y) * width + width - 1 - x] = row[x];
                else dest[size_t(height - 1 - x) * width + y] = row[x];
            }
        }
        context->Unmap(staging.get(), 0);
        if (info.LastMouseUpdateTime.QuadPart) pointer = info.PointerPosition;
        if (info.PointerShapeBufferSize) {
            pointerBytes.resize(info.PointerShapeBufferSize); UINT required = 0;
            hr = duplication->GetFramePointerShape(static_cast<UINT>(pointerBytes.size()), pointerBytes.data(), &required, &shape);
            if (FAILED(hr)) pointerBytes.clear();
        }
        DrawPointer(); ++revision;
        return S_OK;
    }
public:
    explicit DesktopCapture(std::wstring name) : display(std::move(name)) { }
    HRESULT Grab(BYTE* output, int capacity, int* w, int* h, uint64_t& seen) {
        std::lock_guard lock(gate);
        HRESULT hr;
        try { hr = ReadFrame(); }
        catch (const winrt::hresult_error& e) { hr = e.code(); if (Transition(hr)) { Reset(); hr = S_FALSE; } }
        if (FAILED(hr)) return hr;
        if (pixels.empty() || !revision || revision == seen) return S_FALSE;
        *w = width; *h = height;
        if (pixels.size() > static_cast<size_t>(capacity)) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        memcpy(output, pixels.data(), pixels.size()); seen = revision; return S_OK;
    }
    static std::shared_ptr<DesktopCapture> Open(HMONITOR monitor) {
        MONITORINFOEXW info{}; info.cbSize = sizeof(info);
        if (!GetMonitorInfoW(monitor, &info)) winrt::throw_last_error();
        // Only one duplication per process/output. Share it with source thumbnails.
        static std::mutex sharedGate; static std::map<std::wstring, std::weak_ptr<DesktopCapture>> shared;
        std::lock_guard lock(sharedGate); auto& entry = shared[info.szDevice];
        if (auto existing = entry.lock()) return existing;
        auto result = std::make_shared<DesktopCapture>(info.szDevice); entry = result; return result;
    }
};
