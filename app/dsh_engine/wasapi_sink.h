// wasapi_sink.h：不依赖 Qt 的最小 WASAPI 流式输出 sink。
// 支持共享模式（SHARED，走系统混音器，兼容性好）和独占模式（EXCLUSIVE，绕过系统混音器，
// 输出格式与设备协商、可位完美）。二者都把交错的 float32 PCM 送到默认声卡。
#pragma once
#include <cstdint>
#include <string>

class WasapiSink {
public:
    WasapiSink();
    ~WasapiSink();
    WasapiSink(const WasapiSink&) = delete; WasapiSink& operator=(const WasapiSink&) = delete;

    // 用默认设备的混音格式初始化共享输出。成功后 outRate/outCh/outBits 反映混音格式。
    bool initShared(int *outRate, int *outCh, int *outBits);

    // 用默认设备初始化独占输出。desiredRate 为首选流率（通常=源采样率，零 SRC 位完美）；
    // 若设备不支持则按格式协商列表降级。成功后 outRate/outCh/outBits 反映实际协商结果。
    bool initExclusive(int desiredRate, int desiredCh, int *outRate, int *outCh, int *outBits);
    bool start();
    // 快速清空设备缓冲（seek 用）：Stop → Reset → Start，保留已建好的 COM 对象（约 1ms）
    void flush();
    void stop();          // Stop + 释放所有 COM 对象

    // 剩余可用缓冲帧数（设备未消费的部分）
    uint32_t freeFrames() const;
    // 写入 interleaved float32（每帧 outCh 个 float），最多写 min(frames, free)。
    // 返回实际写入的帧数（可能 < frames，若设备缓冲满）。
    // 注意：float32 会按当前协商格式转换（float32 直拷 / 16bit 需换算）后写入。
    int writeFloat(const float *interleaved, int frames);
    bool running() const { return m_running; }

    // 诊断：当前打开的设备名 / 设备缓冲帧数 / 当前 padding
    const char* deviceName() const { return m_devName.c_str(); }
    uint32_t bufferFrames() const { return m_bufferFrames; }
    uint32_t padding() const;   // 实现在 .cpp（头文件不引 WASAPI 头，保持零依赖）
    int outRate() const { return m_outRate; }
    int outCh() const { return m_outCh; }
    int outBits() const { return m_outBits; }
    bool isExclusive() const { return m_exclusive; }

private:
    void *m_client = nullptr;   // IAudioClient*
    void *m_render = nullptr;   // IAudioRenderClient*
    uint32_t m_bufferFrames = 0;
    uint32_t m_bytesPerFrame = 0;
    int m_outRate = 0, m_outCh = 0, m_outBits = 0;
    bool m_running = false;
    bool m_comInit = false;
    bool m_exclusive = false;   // 当前是否为独占模式
    bool m_outIsFloat = true;
    std::string m_devName;   // 当前协商格式是否为 float32（false=16bit PCM，需写时换算）
};
