// render_probe.cpp —— 把引擎里 audio_out.h 的"渲染线程"逻辑原样搬出来做独立实验
// 目的：beep_test（主线程顺序写）能响，而引擎（std::thread + 同样的调用）不响 → 用这个程序二分定位。
//
// 用法：render_probe.exe [mode] [设备名片段]
//   mode=0 主线程顺序写（应等于 beep_test，应能响）
//   mode=1 std::thread 里写（应能响；若不响 → 线程/COM 问题）
//   mode=2 std::thread + 环形缓冲 + 先填静音（引擎当前做法）
//   mode=3 同 2，但事件超时 2000ms（其他都一样）

#define INITGUID
#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <propidl.h>
#include <functiondiscoverykeys_devpkey.h>
#include <ksmedia.h>
#include <cstdio>
#include <cstring>
#include <cmath>
#include <thread>
#include <atomic>
#include <mutex>
#include <vector>
#include <chrono>

static void logline(const char* s) { printf("  %s\n", s); fflush(stdout); }

static IMMDevice* pick(IMMDeviceEnumerator* en, const char* want) {
    if (want && want[0]) {
        IMMDeviceCollection* col = nullptr;
        if (SUCCEEDED(en->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &col)) && col) {
            UINT n = 0; col->GetCount(&n);
            for (UINT i = 0; i < n; i++) {
                IMMDevice* d = nullptr;
                if (FAILED(col->Item(i, &d)) || !d) continue;
                IPropertyStore* ps = nullptr; char nb[256] = {0};
                if (SUCCEEDED(d->OpenPropertyStore(STGM_READ, &ps)) && ps) {
                    PROPVARIANT pv; PropVariantInit(&pv);
                    if (SUCCEEDED(ps->GetValue(PKEY_Device_FriendlyName, &pv)) && pv.vt == VT_LPWSTR && pv.pwszVal)
                        WideCharToMultiByte(CP_UTF8, 0, pv.pwszVal, -1, nb, sizeof(nb) - 1, nullptr, nullptr);
                    PropVariantClear(&pv); ps->Release();
                }
                if (strstr(nb, want)) { col->Release(); return d; }
                d->Release();
            }
            col->Release();
        }
    }
    IMMDevice* d = nullptr;
    if (FAILED(en->GetDefaultAudioEndpoint(eRender, eConsole, &d))) return nullptr;
    return d;
}

struct Runner {
    int mode = 0; const char* dev = "";
    std::atomic<bool> done{false};
    std::mutex rm; std::vector<float> ring; size_t fill = 0, rd = 0, wr = 0;
    std::atomic<uint64_t> frames{0};

    void run() {
        HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        char b[200]; snprintf(b, sizeof(b), "CoInitializeEx hr=0x%08lX", (unsigned long)hr); logline(b);
        IMMDeviceEnumerator* en = nullptr;
        hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, __uuidof(IMMDeviceEnumerator), (void**)&en);
        if (FAILED(hr)) { logline("枚举器失败"); return; }
        IMMDevice* dev2 = pick(en, dev);
        if (!dev2) { logline("没设备"); return; }
        IAudioClient* ac = nullptr;
        dev2->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, (void**)&ac);
        WAVEFORMATEX* mix = nullptr;
        ac->GetMixFormat(&mix);
        snprintf(b, sizeof(b), "混音 %uHz %uch %ubit blockAlign=%u", mix->nSamplesPerSec, mix->nChannels, mix->wBitsPerSample, mix->nBlockAlign);
        logline(b);

        HANDLE ev = (mode == 4) ? nullptr : CreateEventW(nullptr, FALSE, FALSE, nullptr);   // mode4：先不建
        if (mode == 0) {   // 完全照 beep_test：先建事件再 Initialize
            hr = ac->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 0, 0, mix, nullptr);
        } else {           // 引擎的做法：先 Initialize 再建事件
            hr = ac->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 0, 0, mix, nullptr);
        }
        snprintf(b, sizeof(b), "Initialize hr=0x%08lX", (unsigned long)hr); logline(b);
        if (FAILED(hr)) return;
        if (mode == 4) { ev = CreateEventW(nullptr, FALSE, FALSE, nullptr); logline("mode4: 事件在 Initialize 之后创建"); }
        hr = ac->SetEventHandle(ev);
        snprintf(b, sizeof(b), "SetEventHandle hr=0x%08lX", (unsigned long)hr); logline(b);
        UINT32 bufFrames = 0;
        ac->GetBufferSize(&bufFrames);
        IAudioRenderClient* rc = nullptr;
        ac->GetService(__uuidof(IAudioRenderClient), (void**)&rc);
        int outCh = mix->nChannels, outRate = mix->nSamplesPerSec;
        bool isFloat = (mix->wFormatTag == WAVE_FORMAT_IEEE_FLOAT) ||
                       (mix->wFormatTag == WAVE_FORMAT_EXTENSIBLE &&
                        IsEqualGUID(((WAVEFORMATEXTENSIBLE*)mix)->SubFormat, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT));
        if (mode >= 2) { std::lock_guard<std::mutex> lk(rm); ring.assign((size_t)bufFrames * 16 * outCh, 0.0f); }
        hr = ac->Start();
        snprintf(b, sizeof(b), "缓冲 %u 帧(%.1fms)  Start hr=0x%08lX  float=%d", bufFrames, bufFrames * 1000.0 / outRate, (unsigned long)hr, (int)isFloat);
        logline(b);

        std::vector<float> tmp;
        double ph = 0.0;
        auto t0 = std::chrono::steady_clock::now();
        int iters = 0, wakes = 0, timeouts = 0, zeroFree = 0;
        while (!done.load()) {
            DWORD w = WaitForSingleObject(ev, (mode == 3) ? 2000 : 200);
            if (w == WAIT_OBJECT_0) wakes++; else timeouts++;
            iters++;
            if (iters % 20 == 0) {
                snprintf(b, sizeof(b), "iter=%d 事件=%d 超时=%d 已交=%llu 帧", iters, wakes, timeouts, (unsigned long long)frames.load());
                logline(b);
            }
            if (iters > 600) break;
            UINT32 pad = 0;
            if (FAILED(ac->GetCurrentPadding(&pad))) continue;
            UINT32 freeN = bufFrames - pad;
            if (freeN == 0) { zeroFree++; continue; }
            BYTE* p = nullptr;
            if (FAILED(rc->GetBuffer(freeN, &p))) continue;
            tmp.resize((size_t)freeN * outCh);
            if (mode >= 2) {
                size_t got = 0;
                { std::lock_guard<std::mutex> lk(rm);
                  size_t cap = ring.size() / outCh;
                  got = freeN < fill ? freeN : fill;
                  for (size_t i = 0; i < got; i++) { size_t r = (rd % cap) * outCh; for (int c = 0; c < outCh; c++) tmp[i * outCh + c] = ring[r + c]; rd++; fill--; } }
                if (got < freeN) memset(tmp.data() + got * outCh, 0, (size_t)(freeN - got) * outCh * sizeof(float));
            } else {
                for (UINT32 i = 0; i < freeN; i++) {
                    float s = (float)(0.3 * sin(ph)); ph += 2.0 * 3.14159265358979 * 440.0 / outRate;
                    for (int c = 0; c < outCh; c++) tmp[(size_t)i * outCh + c] = s;
                }
            }
            for (UINT32 i = 0; i < freeN; i++) for (int c = 0; c < outCh; c++) {
                float s = tmp[(size_t)i * outCh + c];
                if (isFloat) ((float*)p)[(size_t)i * outCh + c] = s;
                else ((short*)p)[(size_t)i * outCh + c] = (short)(s * 32767.0f);
            }
            rc->ReleaseBuffer(freeN, 0);
            frames.fetch_add(freeN);
        }
        double secs = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
        snprintf(b, sizeof(b), "结束：iters=%d 事件=%d 超时=%d zeroFree=%d 已交=%llu 帧 = %.2f 秒音频 / %.2f 秒墙钟",
                 iters, wakes, timeouts, zeroFree, (unsigned long long)frames.load(), frames.load() / (double)outRate, secs);
        logline(b);
        ac->Stop();
        CloseHandle(ev);
        if (rc) rc->Release();
        if (ac) ac->Release();
        if (dev2) dev2->Release();
        if (en) en->Release();
    }
};

int main(int argc, char** argv) {
    int mode = (argc > 1) ? atoi(argv[1]) : 1;
    const char* dev = (argc > 2) ? argv[2] : "";
    printf("=== render_probe mode=%d dev=%s ===\n", mode, dev);
    Runner r; r.mode = mode; r.dev = dev;
    if (mode == 0) {
        std::thread t([&]{ r.run(); t.join(); });
        std::this_thread::sleep_for(std::chrono::seconds(2));
        r.done = true;
    } else {
        std::thread t([&]{ static_cast<void>(0); });
        std::thread th([&]{ r.run(); });
        std::this_thread::sleep_for(std::chrono::seconds(2));
        r.done = true;
        if (th.joinable()) th.join();
        t.join();
    }
    printf("=== done ===\n");
    return 0;
}
