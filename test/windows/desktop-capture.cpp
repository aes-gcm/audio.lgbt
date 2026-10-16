#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <winrt/base.h>
#include <cstdio>
#include <vector>
#include <array>

// A real DirectX exclusive-fullscreen fixture, exercising the production DLL.
// It never sends input to another application or changes saved display settings.
int wmain(int argc, wchar_t** argv) {
    using namespace winrt;
    SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    auto dll=LoadLibraryW(argc>1?argv[1]:L"dist/native-x64/audio-capture.dll");
    if(!dll)return 1;
    auto start=reinterpret_cast<HRESULT(__cdecl*)(int,void*,void**)>(GetProcAddress(dll,"capture_start"));
    auto grab=reinterpret_cast<HRESULT(__cdecl*)(void*,BYTE*,int,int*,int*)>(GetProcAddress(dll,"capture_grab"));
    auto close=reinterpret_cast<void(__cdecl*)(void*)>(GetProcAddress(dll,"capture_close"));
    auto backend=reinterpret_cast<int(__cdecl*)(void*)>(GetProcAddress(dll,"capture_backend"));
    auto repeat=reinterpret_cast<void(__cdecl*)(void*)>(GetProcAddress(dll,"capture_repeat"));
    if(!start||!grab||!close||!backend||!repeat)return 2;
    WNDCLASSW wc{};wc.lpfnWndProc=DefWindowProcW;wc.hInstance=GetModuleHandleW(nullptr);wc.lpszClassName=L"audio.lgbt-desktop-capture-check";
    RegisterClassW(&wc);
    com_ptr<IDXGIFactory1> factory;check_hresult(CreateDXGIFactory1(__uuidof(IDXGIFactory1),factory.put_void()));
    int monitors=0, outputIndex=0;
    for(UINT a=0;;a++) {
        com_ptr<IDXGIAdapter1> adapter;if(factory->EnumAdapters1(a,adapter.put())==DXGI_ERROR_NOT_FOUND)break;
        for(UINT o=0;;o++) {
            com_ptr<IDXGIOutput> output;if(adapter->EnumOutputs(o,output.put())==DXGI_ERROR_NOT_FOUND)break;
            DXGI_OUTPUT_DESC desc{};check_hresult(output->GetDesc(&desc));if(!desc.AttachedToDesktop)continue;
            if(argc>2 && outputIndex++ != _wtoi(argv[2]))continue;
            auto window=CreateWindowW(wc.lpszClassName,L"audio.lgbt - fullscreen capture test",WS_OVERLAPPEDWINDOW,
                desc.DesktopCoordinates.left+60,desc.DesktopCoordinates.top+60,800,500,nullptr,nullptr,wc.hInstance,nullptr);
            ShowWindow(window,SW_SHOW);ShowWindow(window,SW_SHOW);UpdateWindow(window);SetForegroundWindow(window);
            com_ptr<ID3D11Device> device;com_ptr<ID3D11DeviceContext> context;com_ptr<IDXGISwapChain> swap;com_ptr<ID3D11RenderTargetView> target;
            void* capture=nullptr;void* preview=nullptr;
            try {
                DXGI_SWAP_CHAIN_DESC settings{};settings.BufferDesc.Width=800;settings.BufferDesc.Height=500;
                settings.BufferDesc.Format=DXGI_FORMAT_R8G8B8A8_UNORM;settings.SampleDesc.Count=1;settings.BufferUsage=DXGI_USAGE_RENDER_TARGET_OUTPUT;
                settings.BufferCount=2;settings.OutputWindow=window;settings.Windowed=TRUE;settings.SwapEffect=DXGI_SWAP_EFFECT_DISCARD;
                check_hresult(D3D11CreateDeviceAndSwapChain(adapter.get(),D3D_DRIVER_TYPE_UNKNOWN,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&settings,swap.put(),device.put(),nullptr,context.put()));
                com_ptr<ID3D11Texture2D> surface;check_hresult(swap->GetBuffer(0,__uuidof(ID3D11Texture2D),surface.put_void()));
                check_hresult(device->CreateRenderTargetView(surface.get(),nullptr,target.put()));
                check_hresult(start(0,desc.Monitor,&capture));if(backend(capture)!=2)throw hresult_error(E_FAIL,L"Monitor is not using Desktop Duplication");
                std::vector<BYTE> pixels(4096*4096*4);std::array<int,4> frames{};int w=0,h=0;
                for(int phase=0;phase<3;phase++) {
                    if(phase%2 && GetForegroundWindow()!=window) {
                        printf("Waiting for fixture focus before exclusive fullscreen\n");fflush(stdout);
                        auto until=GetTickCount64()+45000;
                        while(GetForegroundWindow()!=window && GetTickCount64()<until) { MSG msg;while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)){TranslateMessage(&msg);DispatchMessageW(&msg);}Sleep(20); }
                        if(GetForegroundWindow()!=window)throw hresult_error(E_ACCESSDENIED,L"Fixture needs foreground focus for exclusive fullscreen");
                    }
                    printf("phase %d: visible=%d foreground=%d\n",phase,IsWindowVisible(window),GetForegroundWindow()==window);fflush(stdout);
                    HRESULT change=E_PENDING;
                    for(int attempt=0;attempt<30;attempt++) {
                        MSG msg;while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)){TranslateMessage(&msg);DispatchMessageW(&msg);}
                        change=swap->SetFullscreenState(phase%2?TRUE:FALSE,phase%2?output.get():nullptr);
                        if(change!=DXGI_ERROR_NOT_CURRENTLY_AVAILABLE)break;Sleep(100);
                    }
                    if(FAILED(change))printf("SetFullscreen failed phase %d\n",phase);check_hresult(change);
                    const auto until=GetTickCount64()+1600;int frame=0, colored=0;
                    while(GetTickCount64()<until) {
                        MSG msg;while(PeekMessageW(&msg,nullptr,0,0,PM_REMOVE)){TranslateMessage(&msg);DispatchMessageW(&msg);}
                        float color[]={.05f,.15f+float(frame++%40)/200,.3f,1};context->ClearRenderTargetView(target.get(),color);auto present=swap->Present(1,0);if(FAILED(present))printf("Present failed phase %d\n",phase);check_hresult(present);
                        auto hr=grab(capture,pixels.data(),static_cast<int>(pixels.size()),&w,&h);if(FAILED(hr))printf("Grab failed phase %d\n",phase);check_hresult(hr);
                        if(hr==S_OK) {
                            frames[phase]++;
                            RECT area{};GetClientRect(window,&area);POINT center{area.right/2,area.bottom/2};ClientToScreen(window,&center);
                            MONITORINFO monitor{sizeof(MONITORINFO)};GetMonitorInfoW(MonitorFromWindow(window,MONITOR_DEFAULTTONEAREST),&monitor);
                            int x=center.x-monitor.rcMonitor.left,y=center.y-monitor.rcMonitor.top;
                            if(x>=0&&y>=0&&x<w&&y<h){auto p=pixels.data()+(size_t(y)*w+x)*4;if(p[0]>=70&&p[0]<=85&&p[1]>=30&&p[1]<=100&&p[2]>=8&&p[2]<=18)colored++;}
                        }
                    }
                    if(!frames[phase])throw hresult_error(E_FAIL,L"Capture did not recover after fullscreen transition");
                    if(!colored)throw hresult_error(E_FAIL,L"Rendered fullscreen content missing from capture");
                }
                check_hresult(swap->SetFullscreenState(FALSE,nullptr));
                // A thumbnail shares the active monitor capture and gets a cached frame.
                check_hresult(start(0,desc.Monitor,&preview));int previewFrames=0;
                for(int i=0;i<60&&!previewFrames;i++){if(grab(preview,pixels.data(),static_cast<int>(pixels.size()),&w,&h)==S_OK)previewFrames++;Sleep(10);}
                if(!previewFrames)throw hresult_error(E_FAIL,L"Source preview failed during sharing");
                repeat(capture);if(grab(capture,pixels.data(),static_cast<int>(pixels.size()),&w,&h)!=S_OK)throw hresult_error(E_FAIL,L"Static keyframe refresh failed");
                printf("monitor %d: DXGI, window/fullscreen/window frames=%d/%d/%d, preview=%d, %dx%d\n",++monitors,frames[0],frames[1],frames[2],previewFrames,w,h);
            } catch(const hresult_error& e) {
                if(swap)swap->SetFullscreenState(FALSE,nullptr);if(preview)close(preview);if(capture)close(capture);DestroyWindow(window);
                printf("FAILED: 0x%08x\n",static_cast<unsigned>(e.code().value));wprintf(L"%s\n",e.message().c_str());return 3;
            }
            if(preview)close(preview);if(capture)close(capture);DestroyWindow(window);
        }
    }
    printf("PASS: %d monitors\n",monitors);return monitors?0:4;
}
