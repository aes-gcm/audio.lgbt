#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <mmdeviceapi.h>
#include <wrl.h>
#include <mutex>
#include <memory>
#include <vector>
#include "desktop_capture.h"

using namespace winrt;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;
using Microsoft::WRL::ComPtr;
#define API extern "C" __declspec(dllexport)

struct Capture {
    std::shared_ptr<DesktopCapture> desktop;
    uint64_t desktopRevision = 0;
    com_ptr<ID3D11Device> device;
    com_ptr<ID3D11DeviceContext> context;
    com_ptr<ID3D11Texture2D> staging;
    IDirect3DDevice runtimeDevice{nullptr};
    GraphicsCaptureItem item{nullptr};
    Direct3D11CaptureFramePool pool{nullptr};
    GraphicsCaptureSession session{nullptr};
    winrt::Windows::Graphics::SizeInt32 size{};
    ~Capture() { if(session) session.Close(); if(pool) pool.Close(); }
};

API HRESULT __cdecl capture_start(int window, void* handle, Capture** result) noexcept {
    *result = nullptr;
    try {
        auto c = std::make_unique<Capture>();
        if (!window) {
            c->desktop = DesktopCapture::Open(static_cast<HMONITOR>(handle));
            *result = c.release(); return S_OK;
        }
        check_hresult(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION,
            c->device.put(), nullptr, c->context.put()));
        auto dxgi = c->device.as<IDXGIDevice>();
        com_ptr<IInspectable> inspectable;
        check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi.get(), inspectable.put()));
        c->runtimeDevice = inspectable.as<IDirect3DDevice>();
        auto factory = get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        if(window) check_hresult(factory->CreateForWindow((HWND)handle, guid_of<GraphicsCaptureItem>(), put_abi(c->item)));
        else check_hresult(factory->CreateForMonitor((HMONITOR)handle, guid_of<GraphicsCaptureItem>(), put_abi(c->item)));
        c->size = c->item.Size();
        c->pool = Direct3D11CaptureFramePool::CreateFreeThreaded(c->runtimeDevice, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, c->size);
        c->session = c->pool.CreateCaptureSession(c->item);
        // Optional on Windows 10; Windows 11 supports borderless desktop capture.
        // This changes only this session, and still respects Windows policy.
        // Unpackaged Win32 apps use the same API as Microsoft's PowerToys.
        if (auto border = c->session.try_as<IGraphicsCaptureSession3>()) {
            try { border.IsBorderRequired(false); } catch (const hresult_error&) { }
        }
        c->session.StartCapture();
        *result = c.release();
        return S_OK;
    } catch(...) { return to_hresult(); }
}

// Caller owns the capture and serializes grab/close. Only the newest GPU frame is copied.
API HRESULT __cdecl capture_grab(Capture* c, BYTE* output, int capacity, int* width, int* height) noexcept {
    try {
        if (c->desktop) return c->desktop->Grab(output, capacity, width, height, c->desktopRevision);
        auto frame = c->pool.TryGetNextFrame();
        if(!frame) return S_FALSE;
        while(auto newer = c->pool.TryGetNextFrame()) { frame.Close(); frame = newer; }
        const auto size = frame.ContentSize();
        if(size.Width <= 0 || size.Height <= 0) return S_FALSE;
        if(size.Width != c->size.Width || size.Height != c->size.Height) {
            frame.Close(); c->size = size; c->staging = nullptr;
            c->pool.Recreate(c->runtimeDevice, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
            return S_FALSE;
        }
        *width = size.Width; *height = size.Height;
        if(static_cast<int64_t>(size.Width)*size.Height*4 > capacity) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        auto access = frame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        com_ptr<ID3D11Texture2D> texture;
        check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D), texture.put_void()));
        D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
        if(!c->staging) {
            desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0;
            desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ; desc.MiscFlags = 0;
            check_hresult(c->device->CreateTexture2D(&desc, nullptr, c->staging.put()));
        }
        c->context->CopyResource(c->staging.get(), texture.get());
        D3D11_MAPPED_SUBRESOURCE mapped{};
        check_hresult(c->context->Map(c->staging.get(), 0, D3D11_MAP_READ, 0, &mapped));
        for(int y=0;y<size.Height;y++) memcpy(output + size_t(y)*size.Width*4, static_cast<BYTE*>(mapped.pData)+size_t(y)*mapped.RowPitch, size_t(size.Width)*4);
        c->context->Unmap(c->staging.get(),0);
        return S_OK;
    } catch(...) { return to_hresult(); }
}
API void __cdecl capture_close(Capture* c) noexcept { delete c; }
API int __cdecl capture_border_required(Capture* c) noexcept {
    if (c->desktop) return 0;
    try { if (auto border = c->session.try_as<IGraphicsCaptureSession3>()) return border.IsBorderRequired() ? 1 : 0; }
    catch (...) { }
    return -1;
}
API int __cdecl capture_backend(Capture* c) noexcept { return c->desktop ? 2 : 1; }
API void __cdecl capture_repeat(Capture* c) noexcept { c->desktopRevision = 0; }

class Activation final : public Microsoft::WRL::RuntimeClass<Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>, IActivateAudioInterfaceCompletionHandler, Microsoft::WRL::FtmBase> {
public:
    HANDLE done = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HRESULT status = E_PENDING;
    ComPtr<IAudioClient> client;
    ~Activation() { CloseHandle(done); }
    HRESULT STDMETHODCALLTYPE ActivateCompleted(IActivateAudioInterfaceAsyncOperation* operation) override {
        ComPtr<IUnknown> unknown;
        HRESULT activation;
        status = operation->GetActivateResult(&activation, &unknown);
        if(SUCCEEDED(status)) status = activation;
        if(SUCCEEDED(status)) status = unknown.As(&client);
        SetEvent(done);
        return S_OK;
    }
};
struct Loopback {
    ComPtr<IAudioClient> client;
    ComPtr<IAudioCaptureClient> capture;
    ~Loopback() { if(client) client->Stop(); }
};
API HRESULT __cdecl loopback_start(DWORD pid, int exclude, Loopback** result) noexcept {
    *result = nullptr;
    try {
        auto completion = Microsoft::WRL::Make<Activation>();
        AUDIOCLIENT_ACTIVATION_PARAMS params{};
        params.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
        params.ProcessLoopbackParams.TargetProcessId = pid;
        params.ProcessLoopbackParams.ProcessLoopbackMode = exclude ? PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE : PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;
        PROPVARIANT prop{}; prop.vt = VT_BLOB; prop.blob.cbSize = sizeof(params); prop.blob.pBlobData = reinterpret_cast<BYTE*>(&params);
        ComPtr<IActivateAudioInterfaceAsyncOperation> operation;
        check_hresult(ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, __uuidof(IAudioClient), &prop, completion.Get(), &operation));
        if(WaitForSingleObject(completion->done, 10000) != WAIT_OBJECT_0) return HRESULT_FROM_WIN32(WAIT_TIMEOUT);
        check_hresult(completion->status);
        auto c = std::make_unique<Loopback>(); c->client = completion->client;
        WAVEFORMATEX format{WAVE_FORMAT_PCM, 2, 48000, 192000, 4, 16, 0};
        check_hresult(c->client->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY, 200000, 0, &format, nullptr));
        check_hresult(c->client->GetService(IID_PPV_ARGS(&c->capture)));
        check_hresult(c->client->Start()); *result = c.release(); return S_OK;
    } catch(...) { return to_hresult(); }
}
API HRESULT __cdecl loopback_read(Loopback* c, BYTE* output, int capacity, int* bytes) noexcept {
    *bytes = 0;
    try {
        UINT32 frames=0; check_hresult(c->capture->GetNextPacketSize(&frames));
        if(!frames) return S_FALSE;
        if(static_cast<uint64_t>(frames)*4 > capacity) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        BYTE* data; DWORD flags;
        check_hresult(c->capture->GetBuffer(&data, &frames, &flags, nullptr, nullptr));
        *bytes = frames*4;
        if(flags & AUDCLNT_BUFFERFLAGS_SILENT) memset(output,0,*bytes); else memcpy(output,data,*bytes);
        check_hresult(c->capture->ReleaseBuffer(frames)); return S_OK;
    } catch(...) { return to_hresult(); }
}
API void __cdecl loopback_close(Loopback* c) noexcept { delete c; }
