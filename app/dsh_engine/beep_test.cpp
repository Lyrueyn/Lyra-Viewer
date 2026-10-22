#define INITGUID   // ★ 让 PKEY_Device_FriendlyName / KSDATAFORMAT_SUBTYPE_* 这些 GUID 在本文件里真正定义（否则链接缺符号）
// beep_test.cpp —— 全新、最小、事件驱动的 WASAPI 播放测试（完全不依赖老线代码）
//
// 目的：把"到底哪一层没声音"钉死。
//   能听到蜂鸣 → WASAPI/设备/系统都正常 → 问题在我的引擎集成里；
//   听不到     → 问题在设备/系统层（默认设备、蓝牙档位、独占占用、音量等）。
//
// 用法：beep_test.exe [设备名片段] [秒数]
//   例：beep_test.exe             → 默认输出设备，2 秒
//       beep_test.exe S1X 3       → 名字含 S1X 的设备，3 秒
//
// 每一步的 HRESULT 都会打印；失败在哪一步一眼可见。

#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <functiondiscoverykeys_devpkey.h>
#include <propidl.h>
#include <ksmedia.h>
#include <cstdio>
#include <cmath>
#include <cstring>

static void hr(const char* what, HRESULT r) {
    char b[256];
    snprintf(b, sizeof(b), "  %-28s hr=0x%08lX %s", what, (unsigned long)r, SUCCEEDED(r) ? "OK" : "★失败");
    printf("%s\n", b);
}

// 按名字片段找输出设备；找不到或未给 → 用默认
static IMMDevice* pickDevice(IMMDeviceEnumerator* en, const char* want) {
    if (want && want[0]) {
        IMMDeviceCollection* col = nullptr;
        if (SUCCEEDED(en->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &col)) && col) {
            UINT n = 0; col->GetCount(&n);
            printf("  可用输出设备 %u 个：\n", n);
            for (UINT i = 0; i < n; i++) {
                IMMDevice* d = nullptr;
                if (FAILED(col->Item(i, &d)) || !d) continue;
                IPropertyStore* ps = nullptr;
                char nb[256] = {0};
                if (SUCCEEDED(d->OpenPropertyStore(STGM_READ, &ps)) && ps) {
                    PROPVARIANT pv; PropVariantInit(&pv);
                    if (SUCCEEDED(ps->GetValue(PKEY_Device_FriendlyName, &pv)) && pv.vt == VT_LPWSTR && pv.pwszVal)
                        WideCharToMultiByte(CP_UTF8, 0, pv.pwszVal, -1, nb, sizeof(nb) - 1, nullptr, nullptr);
                    PropVariantClear(&pv); ps->Release();
                }
                printf("    [%u] %s\n", i, nb[0] ? nb : "(无名)");
                if (strstr(nb, want)) { col->Release(); return d; }   // 命中
                d->Release();
            }
            col->Release();
        }
        printf("  没找到含 \"%s\" 的设备 → 改用默认设备\n", want);
    }
    IMMDevice* d = nullptr;
    if (FAILED(en->GetDefaultAudioEndpoint(eRender, eConsole, &d))) return nullptr;
    return d;
}

int main(int argc, char** argv) {
    const char* want = (argc > 1) ? argv[1] : "";
    double secs = (argc > 2) ? atof(argv[2]) : 2.0;

    printf("=== WASAPI 蜂鸣测试（全新实现，与播放器无关）===\n");
    HRESULT r = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    hr("CoInitializeEx", r);

    IMMDeviceEnumerator* en = nullptr;
    r = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, __uuidof(IMMDeviceEnumerator), (void**)&en);
    hr("CoCreateInstance(枚举器)", r);
    if (FAILED(r)) return 1;

    IMMDevice* dev = pickDevice(en, want);
    if (!dev) { printf("★ 拿不到输出设备\n"); return 1; }
    {
        IPropertyStore* ps = nullptr; char nb[256] = {0};
        if (SUCCEEDED(dev->OpenPropertyStore(STGM_READ, &ps)) && ps) {
            PROPVARIANT pv; PropVariantInit(&pv);
            if (SUCCEEDED(ps->GetValue(PKEY_Device_FriendlyName, &pv)) && pv.vt == VT_LPWSTR && pv.pwszVal)
                WideCharToMultiByte(CP_UTF8, 0, pv.pwszVal, -1, nb, sizeof(nb) - 1, nullptr, nullptr);
            PropVariantClear(&pv); ps->Release();
        }
        printf("  实际使用设备: %s\n", nb[0] ? nb : "(无名)");
    }

    IAudioClient* ac = nullptr;
    r = dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, (void**)&ac);
    hr("Activate(IAudioClient)", r);
    if (FAILED(r)) return 1;

    WAVEFORMATEX* mix = nullptr;
    r = ac->GetMixFormat(&mix);
    hr("GetMixFormat", r);
    if (FAILED(r) || !mix) return 1;
    bool isFloat = (mix->wFormatTag == WAVE_FORMAT_IEEE_FLOAT) ||
                   (mix->wFormatTag == WAVE_FORMAT_EXTENSIBLE &&
                    ((WAVEFORMATEXTENSIBLE*)mix)->SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT);
    printf("  混音格式: %u Hz  %u 声道  %u bit  %s  blockAlign=%u\n",
           mix->nSamplesPerSec, mix->nChannels, mix->wBitsPerSample,
           isFloat ? "float32" : "PCM", mix->nBlockAlign);

    HANDLE ev = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    r = ac->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 0, 0, mix, nullptr);
    hr("Initialize(共享+事件驱动)", r);
    if (FAILED(r)) return 1;
    r = ac->SetEventHandle(ev);
    hr("SetEventHandle", r);

    UINT32 bufFrames = 0;
    r = ac->GetBufferSize(&bufFrames);
    hr("GetBufferSize", r);
    printf("  设备缓冲: %u 帧 (%.1f ms)\n", bufFrames, bufFrames * 1000.0 / mix->nSamplesPerSec);

    IAudioRenderClient* rc = nullptr;
    r = ac->GetService(__uuidof(IAudioRenderClient), (void**)&rc);
    hr("GetService(IAudioRenderClient)", r);
    if (FAILED(r)) return 1;

    r = ac->Start();
    hr("Start", r);
    if (FAILED(r)) return 1;

    // 直接按混音格式生成 440Hz 正弦（不需要重采样），左右声道同相
    const int rate = mix->nSamplesPerSec;
    const int ch = mix->nChannels;
    const int bps = mix->wBitsPerSample / 8;
    const long long total = (long long)(secs * rate);
    long long done = 0;
    double phase = 0.0, step = 2.0 * 3.14159265358979 * 440.0 / rate;
    int waits = 0;
    while (done < total) {
        DWORD w = WaitForSingleObject(ev, 2000);
        if (w != WAIT_OBJECT_0) { printf("★ 等待事件失败/超时 (w=%lu)\n", (unsigned long)w); break; }
        waits++;
        UINT32 pad = 0;
        if (FAILED(ac->GetCurrentPadding(&pad))) { printf("★ GetCurrentPadding 失败\n"); break; }
        UINT32 free = bufFrames - pad;
        if (free == 0) continue;
        BYTE* p = nullptr;
        if (FAILED(rc->GetBuffer(free, &p))) { printf("★ GetBuffer 失败\n"); break; }
        for (UINT32 i = 0; i < free; i++) {
            float v = (float)(0.35 * sin(phase));   // -9dB 左右，不刺耳
            phase += step; if (phase > 6.28318530717959) phase -= 6.28318530717959;
            for (int c = 0; c < ch; c++) {
                if (isFloat) ((float*)p)[(size_t)i * ch + c] = v;
                else if (bps == 2) ((short*)p)[(size_t)i * ch + c] = (short)(v * 32767);
                else ((int*)p)[(size_t)i * ch + c] = (int)(v * 2147483647.0);
            }
        }
        rc->ReleaseBuffer(free, 0);
        done += free;
    }
    printf("  已写入 %lld 帧 / 期望 %lld 帧（%.2f 秒），事件唤醒 %d 次\n", done, total, secs, waits);

    ac->Stop();
    printf("=== 结束（如果这 2 秒没响，问题就不在播放器代码里）===\n");
    rc->Release(); ac->Release(); dev->Release(); en->Release();
    CloseHandle(ev);
    return 0;
}
