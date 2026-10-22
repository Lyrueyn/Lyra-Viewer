// vk_probe2.cpp —— 反向互通验证：D3D11 建共享纹理 → Vulkan 导入并渲染 → D3D11 读回
//
// 为什么换方向：探针 1 证明"Vulkan 建 + 导出句柄 + D3D11 OpenSharedResource"在这台机器上
// 报 E_INVALIDARG（驱动报告三种句柄类型都支持、导出也成功，但 D3D11 打不开）。
// 反向（D3D11 建、Vulkan 导入）是规范里 D3D11→Vulkan 的官方方向，也是老线"共享纹理 + D3D11 呈现"的做法：
//   D3D11 建 BGRA8 纹理（MISC_SHARED_NTHANDLE + MISC_SHARED）→ CreateSharedHandle 得 NT 句柄
//   → Vulkan: VkImportMemoryWin32HandleInfoKHR(D3D11_TEXTURE) → 绑定 VkImage → 清成已知色
//   → D3D11 读回校验（同一个 GUID 的 KeyedMutex 同步，或简单等 Vulkan 队列空闲）
//
// 编译：
//   g++ -std=c++17 -O2 vk_probe2.cpp -o vk_probe2.exe -I"<repo>\Vulkan-Headers-main\include" \
//       -DVK_NO_PROTOTYPES -DVK_USE_PLATFORM_WIN32_KHR -ld3d11 -ldxgi -luuid -lole32 -lgdi32
// 运行：vk_probe2.exe [out.bmp]

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <vector>
#include <string>

#include <vulkan/vulkan.h>

static const int W = 256, H = 144;
static const float CR = 0x11 / 255.0f, CG = 0x22 / 255.0f, CB = 0x33 / 255.0f;

static HMODULE g_vk = nullptr;
static PFN_vkGetInstanceProcAddr g_gipa = nullptr;   // 只用它 + vkGetDeviceProcAddr 就能拿全

template <typename T> static void L(const char* n, T& fn, PFN_vkGetInstanceProcAddr g, VkInstance i) {
    fn = (T)g(i, n);
    if (!fn) printf("  [x] 取不到 %s\n", n);
}
template <typename T> static void LD(const char* n, T& fn, PFN_vkGetDeviceProcAddr g, VkDevice d) {
    fn = (T)g(d, n);
    if (!fn) printf("  [x] 取不到(device) %s\n", n);
}

int main(int argc, char** argv) {
    setvbuf(stdout, nullptr, _IONBF, 0);
    const char* outBmp = (argc > 1) ? argv[1] : "vk_probe2.bmp";
    printf("=== 反向互通：D3D11 建共享纹理 → Vulkan 导入渲染 → D3D11 读回 ===\n");

    // ---------- 1. D3D11 设备 + 共享纹理（NT 句柄） ----------
    ID3D11Device* d3d = nullptr; ID3D11DeviceContext* ctx = nullptr; D3D_FEATURE_LEVEL fl{};
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                   nullptr, 0, D3D11_SDK_VERSION, &d3d, &fl, &ctx);
    if (FAILED(hr)) { printf("[x] D3D11CreateDevice: 0x%08lX\n", (unsigned long)hr); return 2; }
    IDXGIDevice* dxdev = nullptr; d3d->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxdev);
    IDXGIAdapter* ad = nullptr; dxdev->GetAdapter(&ad);
    DXGI_ADAPTER_DESC adesc{}; ad->GetDesc(&adesc);
    printf("[1] D3D11 适配器: %ls  LUID=%08lX:%08lX\n", adesc.Description,
           (unsigned long)adesc.AdapterLuid.HighPart, (unsigned long)adesc.AdapterLuid.LowPart);

    D3D11_TEXTURE2D_DESC td{};
    td.Width = W; td.Height = H; td.MipLevels = 1; td.ArraySize = 1;
    td.Format = DXGI_FORMAT_B8G8R8A8_UNORM; td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    td.MiscFlags = D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE;
    ID3D11Texture2D* tex = nullptr;
    hr = d3d->CreateTexture2D(&td, nullptr, &tex);
    if (FAILED(hr)) { printf("[x] CreateTexture2D(共享): 0x%08lX\n", (unsigned long)hr); return 2; }
    IDXGIResource1* res1 = nullptr;
    if (FAILED(tex->QueryInterface(__uuidof(IDXGIResource1), (void**)&res1))) { printf("[x] 拿不到 IDXGIResource1\n"); return 2; }
    HANDLE nt = nullptr;
    hr = res1->CreateSharedHandle(nullptr, DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE, nullptr, &nt);
    if (FAILED(hr)) { printf("[x] CreateSharedHandle: 0x%08lX\n", (unsigned long)hr); return 2; }
    printf("[1] D3D11 共享纹理 %dx%d 已建，NT 句柄=%p\n", W, H, nt);

    // KeyedMutex：跨 API 同步用（Vulkan 导入后 acquire/release）
    IDXGIKeyedMutex* km = nullptr;
    bool hasKm = SUCCEEDED(tex->QueryInterface(__uuidof(IDXGIKeyedMutex), (void**)&km));
    printf("[1] KeyedMutex: %s\n", hasKm ? "有" : "无");

    // ---------- 2. Vulkan：instance/device ----------
    g_vk = LoadLibraryA("vulkan-1.dll");
    if (!g_vk) { printf("[x] 无 vulkan-1.dll\n"); return 3; }
    g_gipa = (PFN_vkGetInstanceProcAddr)GetProcAddress(g_vk, "vkGetInstanceProcAddr");
    PFN_vkCreateInstance createInst = (PFN_vkCreateInstance)g_gipa(nullptr, "vkCreateInstance");
    const char* iext[] = { VK_KHR_EXTERNAL_MEMORY_CAPABILITIES_EXTENSION_NAME };
    VkApplicationInfo app{ VK_STRUCTURE_TYPE_APPLICATION_INFO };
    app.pApplicationName = "dsh vk probe2"; app.apiVersion = VK_API_VERSION_1_2;
    VkInstanceCreateInfo ici{ VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO };
    ici.pApplicationInfo = &app; ici.enabledExtensionCount = 1; ici.ppEnabledExtensionNames = iext;
    VkInstance inst = VK_NULL_HANDLE;
    if (createInst(&ici, nullptr, &inst) != VK_SUCCESS) { printf("[x] vkCreateInstance\n"); return 3; }

    PFN_vkEnumeratePhysicalDevices enumPd; L("vkEnumeratePhysicalDevices", enumPd, g_gipa, inst);
    PFN_vkGetPhysicalDeviceProperties gpp; L("vkGetPhysicalDeviceProperties", gpp, g_gipa, inst);
    PFN_vkGetPhysicalDeviceQueueFamilyProperties gqf; L("vkGetPhysicalDeviceQueueFamilyProperties", gqf, g_gipa, inst);
    PFN_vkCreateDevice createDev; L("vkCreateDevice", createDev, g_gipa, inst);
    PFN_vkGetDeviceProcAddr gdpa; L("vkGetDeviceProcAddr", gdpa, g_gipa, inst);
    PFN_vkDestroyInstance destroyInst; L("vkDestroyInstance", destroyInst, g_gipa, inst);
    if (!enumPd || !gpp || !gqf || !createDev || !gdpa) return 3;

    uint32_t n = 0; enumPd(inst, &n, nullptr);
    std::vector<VkPhysicalDevice> pds(n); enumPd(inst, &n, pds.data());
    VkPhysicalDevice pd = VK_NULL_HANDLE; int qf = -1;
    for (auto d : pds) {
        VkPhysicalDeviceProperties p{}; gpp(d, &p);
        uint32_t qn = 0; gqf(d, &qn, nullptr);
        std::vector<VkQueueFamilyProperties> qs(qn); gqf(d, &qn, qs.data());
        int gi = -1;
        for (uint32_t i = 0; i < qn; i++) if (qs[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) { gi = (int)i; break; }
        printf("    设备: %-36s type=%d 图形队列=%d\n", p.deviceName, (int)p.deviceType, gi);
        if (gi >= 0 && (pd == VK_NULL_HANDLE || p.deviceType == VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU)) { pd = d; qf = gi; }
    }
    if (pd == VK_NULL_HANDLE) { printf("[x] 无可用设备\n"); return 3; }

    // 只启用确实支持的扩展
    PFN_vkEnumerateDeviceExtensionProperties edep; L("vkEnumerateDeviceExtensionProperties", edep, g_gipa, inst);
    uint32_t dn = 0; edep(pd, nullptr, &dn, nullptr);
    std::vector<VkExtensionProperties> dext(dn); edep(pd, nullptr, &dn, dext.data());
    auto has = [&](const char* s) { for (auto& e : dext) if (!strcmp(e.extensionName, s)) return true; return false; };
    std::vector<const char*> devExt;
    if (has(VK_KHR_EXTERNAL_MEMORY_EXTENSION_NAME)) devExt.push_back(VK_KHR_EXTERNAL_MEMORY_EXTENSION_NAME);
    if (has(VK_KHR_EXTERNAL_MEMORY_WIN32_EXTENSION_NAME)) devExt.push_back(VK_KHR_EXTERNAL_MEMORY_WIN32_EXTENSION_NAME);
    if (has(VK_KHR_DEDICATED_ALLOCATION_EXTENSION_NAME)) devExt.push_back(VK_KHR_DEDICATED_ALLOCATION_EXTENSION_NAME);
    if (has(VK_KHR_GET_MEMORY_REQUIREMENTS_2_EXTENSION_NAME)) devExt.push_back(VK_KHR_GET_MEMORY_REQUIREMENTS_2_EXTENSION_NAME);
    float pri = 1.0f;
    VkDeviceQueueCreateInfo qci{ VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO };
    qci.queueFamilyIndex = (uint32_t)qf; qci.queueCount = 1; qci.pQueuePriorities = &pri;
    VkDeviceCreateInfo dci{ VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO };
    dci.queueCreateInfoCount = 1; dci.pQueueCreateInfos = &qci;
    dci.enabledExtensionCount = (uint32_t)devExt.size(); dci.ppEnabledExtensionNames = devExt.data();
    VkDevice dev = VK_NULL_HANDLE;
    if (createDev(pd, &dci, nullptr, &dev) != VK_SUCCESS) { printf("[x] vkCreateDevice\n"); return 3; }
    printf("[2] Vulkan 设备就绪（%s）\n", devExt.size() ? "含 external_memory_win32" : "无外部内存扩展");

    PFN_vkGetDeviceQueue getQ; LD("vkGetDeviceQueue", getQ, gdpa, dev);
    PFN_vkCreateImage crImage; LD("vkCreateImage", crImage, gdpa, dev);
    PFN_vkGetImageMemoryRequirements imgReq; LD("vkGetImageMemoryRequirements", imgReq, gdpa, dev);
    PFN_vkAllocateMemory allocMem; LD("vkAllocateMemory", allocMem, gdpa, dev);
    PFN_vkBindImageMemory bindImg; LD("vkBindImageMemory", bindImg, gdpa, dev);
    PFN_vkCreateCommandPool crPool; LD("vkCreateCommandPool", crPool, gdpa, dev);
    PFN_vkAllocateCommandBuffers allocCb; LD("vkAllocateCommandBuffers", allocCb, gdpa, dev);
    PFN_vkBeginCommandBuffer beginCb; LD("vkBeginCommandBuffer", beginCb, gdpa, dev);
    PFN_vkCmdPipelineBarrier barrier; LD("vkCmdPipelineBarrier", barrier, gdpa, dev);
    PFN_vkCmdClearColorImage clearImg; LD("vkCmdClearColorImage", clearImg, gdpa, dev);
    PFN_vkEndCommandBuffer endCb; LD("vkEndCommandBuffer", endCb, gdpa, dev);
    PFN_vkQueueSubmit submit; LD("vkQueueSubmit", submit, gdpa, dev);
    PFN_vkQueueWaitIdle waitIdle; LD("vkQueueWaitIdle", waitIdle, gdpa, dev);
    PFN_vkGetMemoryWin32HandlePropertiesKHR memProps;
    LD("vkGetMemoryWin32HandlePropertiesKHR", memProps, gdpa, dev);
    if (!getQ || !crImage || !allocMem || !bindImg || !crPool || !allocCb || !beginCb || !clearImg || !endCb || !submit || !waitIdle) return 4;
    VkQueue queue = VK_NULL_HANDLE; getQ(dev, (uint32_t)qf, 0, &queue);

    // ---------- 3. 导入 D3D11 的 NT 句柄 ----------
    if (memProps) {
        VkMemoryWin32HandlePropertiesKHR hp{ VK_STRUCTURE_TYPE_MEMORY_WIN32_HANDLE_PROPERTIES_KHR };
        VkResult rr = memProps(dev, VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT, nt, &hp);
        printf("[3a] vkGetMemoryWin32HandleProperties: r=%d memoryTypeBits=0x%X\n", (int)rr, hp.memoryTypeBits);
        if (rr != VK_SUCCESS) { printf("[x] 该句柄 Vulkan 不接受\n"); return 5; }
    }
    VkImportMemoryWin32HandleInfoKHR imp{ VK_STRUCTURE_TYPE_IMPORT_MEMORY_WIN32_HANDLE_INFO_KHR };
    imp.handleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT;
    imp.handle = nt;
    VkExternalMemoryImageCreateInfo extImg{ VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO };
    extImg.pNext = nullptr;
    extImg.handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT;
    VkImageCreateInfo iciImg{ VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO };
    iciImg.pNext = &extImg;
    iciImg.imageType = VK_IMAGE_TYPE_2D;
    iciImg.format = VK_FORMAT_B8G8R8A8_UNORM;
    iciImg.extent = { (uint32_t)W, (uint32_t)H, 1 };
    iciImg.mipLevels = 1; iciImg.arrayLayers = 1; iciImg.samples = VK_SAMPLE_COUNT_1_BIT;
    iciImg.tiling = VK_IMAGE_TILING_OPTIMAL;
    iciImg.usage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_SAMPLED_BIT;
    iciImg.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    iciImg.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    VkImage img = VK_NULL_HANDLE;
    VkResult r = crImage(dev, &iciImg, nullptr, &img);
    if (r != VK_SUCCESS) { printf("[x] vkCreateImage(导入用): %d\n", (int)r); return 5; }

    VkMemoryRequirements mr{}; imgReq(dev, img, &mr);
    // 导入的内存：大小必须与 D3D11 纹理的分配一致，用句柄属性给的 memoryTypeBits 选类型
    VkMemoryWin32HandlePropertiesKHR hp2{ VK_STRUCTURE_TYPE_MEMORY_WIN32_HANDLE_PROPERTIES_KHR };
    if (memProps) memProps(dev, VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT, nt, &hp2);
    VkPhysicalDeviceMemoryProperties mp{};
    PFN_vkGetPhysicalDeviceMemoryProperties gmp = (PFN_vkGetPhysicalDeviceMemoryProperties)g_gipa(inst, "vkGetPhysicalDeviceMemoryProperties");
    gmp(pd, &mp);
    uint32_t mti = UINT32_MAX;
    for (uint32_t i = 0; i < mp.memoryTypeCount; i++)
        if ((hp2.memoryTypeBits & (1u << i)) && (mp.memoryTypes[i].propertyFlags & VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT)) { mti = i; break; }
    if (mti == UINT32_MAX) for (uint32_t i = 0; i < mp.memoryTypeCount; i++) if (hp2.memoryTypeBits & (1u << i)) { mti = i; break; }
    printf("[3b] 导入内存: size=%llu（VkImage 需要 %llu）memoryType=%u\n",
           (unsigned long long)mr.size, (unsigned long long)mr.size, mti);
    VkMemoryDedicatedAllocateInfo ded{ VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO };
    ded.image = img; ded.pNext = &imp;
    VkMemoryAllocateInfo mai{ VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO };
    mai.pNext = &ded; mai.allocationSize = mr.size; mai.memoryTypeIndex = mti;
    VkDeviceMemory mem = VK_NULL_HANDLE;
    r = allocMem(dev, &mai, nullptr, &mem);
    if (r != VK_SUCCESS) { printf("[x] vkAllocateMemory(导入): %d（0x%X=内存类型不支持？）\n", (int)r, mti); return 5; }
    r = bindImg(dev, img, mem, 0);
    if (r != VK_SUCCESS) { printf("[x] vkBindImageMemory: %d\n", (int)r); return 5; }
    printf("[3] 已把 D3D11 的共享纹理导入成 VkImage\n");

    // ---------- 4. Vulkan 清成已知色 ----------
    VkCommandPoolCreateInfo pci{ VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO };
    pci.queueFamilyIndex = (uint32_t)qf;
    VkCommandPool pool = VK_NULL_HANDLE; crPool(dev, &pci, nullptr, &pool);
    VkCommandBufferAllocateInfo cbai{ VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO };
    cbai.commandPool = pool; cbai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY; cbai.commandBufferCount = 1;
    VkCommandBuffer cb = VK_NULL_HANDLE; allocCb(dev, &cbai, &cb);
    VkCommandBufferBeginInfo cbbi{ VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO };
    cbbi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    beginCb(cb, &cbbi);
    VkImageSubresourceRange rng{ VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
    VkImageMemoryBarrier b{ VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER };
    b.oldLayout = VK_IMAGE_LAYOUT_UNDEFINED; b.newLayout = VK_IMAGE_LAYOUT_GENERAL;
    b.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED; b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.image = img; b.subresourceRange = rng;
    b.srcAccessMask = 0; b.dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
    if (barrier) barrier(cb, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, nullptr, 0, nullptr, 1, &b);
    VkClearColorValue cc{}; cc.float32[0] = CR; cc.float32[1] = CG; cc.float32[2] = CB; cc.float32[3] = 1.0f;
    clearImg(cb, img, VK_IMAGE_LAYOUT_GENERAL, &cc, 1, &rng);
    endCb(cb);
    VkSubmitInfo si{ VK_STRUCTURE_TYPE_SUBMIT_INFO };
    si.commandBufferCount = 1; si.pCommandBuffers = &cb;
    submit(queue, 1, &si, VK_NULL_HANDLE);
    waitIdle(queue);
    printf("[4] Vulkan 已把这块共享纹理清成 RGBA=(0x%02X,0x%02X,0x%02X)\n", 0x11, 0x22, 0x33);

    // ---------- 5. D3D11 读回 ----------
    D3D11_TEXTURE2D_DESC sd = td;
    sd.Usage = D3D11_USAGE_STAGING; sd.BindFlags = 0; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ; sd.MiscFlags = 0;
    ID3D11Texture2D* stage = nullptr;
    hr = d3d->CreateTexture2D(&sd, nullptr, &stage);
    if (FAILED(hr)) { printf("[x] staging: 0x%08lX\n", (unsigned long)hr); return 6; }
    ctx->CopyResource(stage, tex);
    D3D11_MAPPED_SUBRESOURCE ms{};
    hr = ctx->Map(stage, 0, D3D11_MAP_READ, 0, &ms);
    if (FAILED(hr)) { printf("[x] Map: 0x%08lX\n", (unsigned long)hr); return 6; }
    const uint8_t* px = (const uint8_t*)ms.pData;
    uint8_t B = px[0], G = px[1], R = px[2], A = px[3];
    bool ok = (R == 0x11 && G == 0x22 && B == 0x33 && A == 255);
    printf("[5] D3D11 读回(BGRA)=(%u,%u,%u,%u) 期望=(0x33,0x22,0x11,255) → %s\n", B, G, R, A, ok ? "一致 ✔" : "不一致 ✘");

    {   // 存 BMP 留证据
        int rowRaw = (int)ms.RowPitch, rowOut = (W * 3 + 3) & ~3, dataSize = rowOut * H, fileSize = 54 + dataSize;
        std::vector<uint8_t> h(fileSize, 0);
        h[0] = 'B'; h[1] = 'M'; *(uint32_t*)&h[2] = fileSize; *(uint32_t*)&h[10] = 54;
        *(uint32_t*)&h[14] = 40; *(int32_t*)&h[18] = W; *(int32_t*)&h[22] = H;
        *(uint16_t*)&h[26] = 1; *(uint16_t*)&h[28] = 24;
        for (int y = 0; y < H; y++) {
            const uint8_t* s = px + (size_t)(H - 1 - y) * rowRaw;
            uint8_t* d = h.data() + 54 + (size_t)y * rowOut;
            for (int x = 0; x < W; x++) { d[x * 3 + 0] = s[x * 4 + 0]; d[x * 3 + 1] = s[x * 4 + 1]; d[x * 3 + 2] = s[x * 4 + 2]; }
        }
        FILE* f = fopen(outBmp, "wb"); if (f) { fwrite(h.data(), 1, h.size(), f); fclose(f); printf("[5] 已写出 %s\n", outBmp); }
    }
    ctx->Unmap(stage, 0);

    printf("\n=== 结论: %s ===\n", ok ? "✔ D3D11 建 → Vulkan 导入渲染 → D3D11 读回 颜色一致，反向互通可行"
                                    : "✘ 颜色不一致");
    stage->Release(); tex->Release(); if (res1) res1->Release(); if (km) km->Release();
    ctx->Release(); d3d->Release(); ad->Release(); dxdev->Release();
    if (nt) CloseHandle(nt);
    return ok ? 0 : 1;
}
