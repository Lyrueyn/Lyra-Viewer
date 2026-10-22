// vk_probe.cpp —— Vulkan 后端可行性验证（第一步：只证明"Vulkan 画的东西 D3D11 能拿到"）
//
// 为什么先做这个：WinUI 的 SwapChainPanel 只接受 DXGI 交换链，Vulkan 不能直接 present 进去。
// 所以当前线的 Vulkan 方案必须是：Vulkan 渲染视频 → 导出共享 NT 句柄(KMT) → D3D11 打开该纹理
// → D3D11 叠弹幕/呈现。这条互操作是全案最大的风险点，先单独证掉。
//
// 本实验（不需要任何着色器）：
//   1) 动态加载 vulkan-1.dll（MinGW 下不折腾导入库）
//   2) 建 instance/物理设备/逻辑设备（启用 VK_KHR_external_memory_win32）
//   3) 建一张 BGRA8 的 VkImage，内存用 VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_WIN32 导出 → 拿 NT 句柄
//   4) vkCmdClearColorImage 清成一个已知颜色
//   5) D3D11: OpenSharedResource1(句柄) → CopyResource 到 staging → Map 读回 → 校验颜色 → 存 BMP
//
// 编译：
//   g++ -std=c++17 -O2 vk_probe.cpp -o vk_probe.exe \
//       -I"<repo>\Vulkan-Headers-main\include" -DVK_NO_PROTOTYPES \
//       -ld3d11 -ldxgi -luuid -lole32 -lgdi32
//
// 运行：vk_probe.exe [out.bmp]   退出码 0=互通成功

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <d3d11_1.h>     // ID3D11Device1::OpenSharedResource1（Win8+）
#include <dxgi1_2.h>
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <vector>
#include <string>

#include <vulkan/vulkan.h>

static const int W = 256, H = 144;
// 期望颜色（BGRA 顺序存）：R=0x11 G=0x22 B=0x33 A=0xFF
static const float CLEAR_R = 0x11 / 255.0f, CLEAR_G = 0x22 / 255.0f, CLEAR_B = 0x33 / 255.0f, CLEAR_A = 1.0f;

// ---------------- 动态加载 Vulkan ----------------
static HMODULE g_vk = nullptr;
static PFN_vkGetInstanceProcAddr g_gipa = nullptr;

template <typename T> static bool load(const char* name, T& fn) {
    fn = (T)g_gipa(nullptr, name);
    if (!fn) { printf("  [x] 取不到全局函数 %s\n", name); return false; }
    return true;
}
// ★ Vulkan 的分层：vkGetInstanceProcAddr(NULL) 只给全局函数；instance 级要带 instance 句柄；
//   设备级（vkCreateImage/vkCmd* 等）用 vkGetDeviceProcAddr(device) 最稳。
template <typename T> static bool loadInst(VkInstance inst, const char* name, T& fn) {
    fn = (T)g_gipa(inst, name);
    if (!fn) { printf("  [x] 取不到 instance 级函数 %s\n", name); return false; }
    return true;
}
template <typename T> static bool loadDev(VkDevice dev, PFN_vkGetDeviceProcAddr gdpa, const char* name, T& fn) {
    fn = (T)gdpa(dev, name);
    if (!fn) { printf("  [x] 取不到设备级函数 %s\n", name); return false; }
    return true;
}

struct Fn {
    PFN_vkCreateInstance CreateInstance;
    PFN_vkEnumerateInstanceExtensionProperties EnumerateInstanceExtensionProperties;
    PFN_vkEnumeratePhysicalDevices EnumeratePhysicalDevices;
    PFN_vkGetPhysicalDeviceProperties GetPhysicalDeviceProperties;
    PFN_vkGetPhysicalDeviceQueueFamilyProperties GetPhysicalDeviceQueueFamilyProperties;
    PFN_vkGetPhysicalDeviceFeatures GetPhysicalDeviceFeatures;
    PFN_vkCreateDevice CreateDevice;
    PFN_vkGetDeviceProcAddr GetDeviceProcAddr;
    PFN_vkGetDeviceQueue GetDeviceQueue;
    PFN_vkCreateImage CreateImage;
    PFN_vkGetImageMemoryRequirements GetImageMemoryRequirements;
    PFN_vkAllocateMemory AllocateMemory;
    PFN_vkBindImageMemory BindImageMemory;
    PFN_vkCreateCommandPool CreateCommandPool;
    PFN_vkAllocateCommandBuffers AllocateCommandBuffers;
    PFN_vkBeginCommandBuffer BeginCommandBuffer;
    PFN_vkCmdClearColorImage CmdClearColorImage;
    PFN_vkEndCommandBuffer EndCommandBuffer;
    PFN_vkQueueSubmit QueueSubmit;
    PFN_vkQueueWaitIdle QueueWaitIdle;
    PFN_vkDestroyImage DestroyImage;
    PFN_vkFreeMemory FreeMemory;
    PFN_vkDestroyCommandPool DestroyCommandPool;
    PFN_vkDestroyDevice DestroyDevice;
    PFN_vkDestroyInstance DestroyInstance;
    // device 级（用 GetDeviceProcAddr 取）
    PFN_vkGetMemoryWin32HandleKHR GetMemoryWin32HandleKHR;
};
static Fn vk{};

// 用 VkPhysicalDeviceProperties 打印显卡名（helper）
static std::string dev_name(VkPhysicalDevice pd) {
    VkPhysicalDeviceProperties p{};
    vk.GetPhysicalDeviceProperties(pd, &p);
    return std::string(p.deviceName);
}

int main(int argc, char** argv) {
    setvbuf(stdout, nullptr, _IONBF, 0);   // 无缓冲：崩溃时也能看到走到哪一步
    const char* outBmp = (argc > 1) ? argv[1] : "vk_probe.bmp";
    printf("=== Vulkan <-> D3D11 共享纹理互通验证 ===\n");

    // ---------- 1. 动态加载 vulkan-1.dll ----------
    g_vk = LoadLibraryA("vulkan-1.dll");
    if (!g_vk) { printf("[x] 找不到 vulkan-1.dll（未安装 Vulkan 运行时？）\n"); return 2; }
    g_gipa = (PFN_vkGetInstanceProcAddr)GetProcAddress(g_vk, "vkGetInstanceProcAddr");
    if (!g_gipa) { printf("[x] vulkan-1.dll 里没有 vkGetInstanceProcAddr\n"); return 2; }
    bool ok = true;
    // 全局函数（不带 instance 就能取）
    ok &= load("vkCreateInstance", vk.CreateInstance);
    ok &= load("vkEnumerateInstanceExtensionProperties", vk.EnumerateInstanceExtensionProperties);
    if (!ok) return 2;
    printf("[1] vulkan-1.dll 加载成功\n");

    // ---------- 2. instance ----------
    // 需要 VK_KHR_external_memory_capabilities（instance 级）才能查询外部内存能力
    uint32_t next = 0;
    vk.EnumerateInstanceExtensionProperties(nullptr, &next, nullptr);
    std::vector<VkExtensionProperties> iext(next);
    vk.EnumerateInstanceExtensionProperties(nullptr, &next, iext.data());
    bool hasCap = false;
    for (auto& e : iext) if (!strcmp(e.extensionName, VK_KHR_EXTERNAL_MEMORY_CAPABILITIES_EXTENSION_NAME)) hasCap = true;
    printf("[2] instance 扩展: external_memory_capabilities=%s\n", hasCap ? "有" : "无");
    if (!hasCap) return 3;

    const char* instExt[] = { VK_KHR_EXTERNAL_MEMORY_CAPABILITIES_EXTENSION_NAME };
    VkApplicationInfo app{ VK_STRUCTURE_TYPE_APPLICATION_INFO };
    app.pApplicationName = "dsh vk probe";
    app.apiVersion = VK_API_VERSION_1_2;
    VkInstanceCreateInfo ici{ VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO };
    ici.pApplicationInfo = &app;
    ici.enabledExtensionCount = 1;
    ici.ppEnabledExtensionNames = instExt;
    VkInstance inst = VK_NULL_HANDLE;
    if (vk.CreateInstance(&ici, nullptr, &inst) != VK_SUCCESS) { printf("[x] vkCreateInstance 失败\n"); return 3; }
    printf("[2] instance 创建成功\n");

    // instance 级函数：必须带 instance 句柄取
    ok = true;
    ok &= loadInst(inst, "vkEnumeratePhysicalDevices", vk.EnumeratePhysicalDevices);
    ok &= loadInst(inst, "vkGetPhysicalDeviceProperties", vk.GetPhysicalDeviceProperties);
    ok &= loadInst(inst, "vkGetPhysicalDeviceQueueFamilyProperties", vk.GetPhysicalDeviceQueueFamilyProperties);
    ok &= loadInst(inst, "vkGetPhysicalDeviceFeatures", vk.GetPhysicalDeviceFeatures);
    ok &= loadInst(inst, "vkCreateDevice", vk.CreateDevice);
    ok &= loadInst(inst, "vkGetDeviceProcAddr", vk.GetDeviceProcAddr);
    ok &= loadInst(inst, "vkDestroyInstance", vk.DestroyInstance);
    if (!ok) return 3;

    // ---------- 3. 物理设备：优先独显，且要支持 external_memory_win32 ----------
    uint32_t n = 0; vk.EnumeratePhysicalDevices(inst, &n, nullptr);
    if (!n) { printf("[x] 没有 Vulkan 物理设备\n"); return 4; }
    std::vector<VkPhysicalDevice> pds(n); vk.EnumeratePhysicalDevices(inst, &n, pds.data());
    VkPhysicalDevice pd = VK_NULL_HANDLE; int qf = -1;
    for (auto d : pds) {
        VkPhysicalDeviceProperties p{}; vk.GetPhysicalDeviceProperties(d, &p);
        uint32_t qn = 0; vk.GetPhysicalDeviceQueueFamilyProperties(d, &qn, nullptr);
        std::vector<VkQueueFamilyProperties> qs(qn); vk.GetPhysicalDeviceQueueFamilyProperties(d, &qn, qs.data());
        int gi = -1;
        for (uint32_t i = 0; i < qn; i++)
            if ((qs[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) && (qs[i].queueFlags & VK_QUEUE_TRANSFER_BIT)) { gi = (int)i; break; }
        printf("  设备: %-40s type=%d 队列族=%u 图形队列=%d\n", p.deviceName, (int)p.deviceType, qn, gi);
        if (gi >= 0 && (pd == VK_NULL_HANDLE || p.deviceType == VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU)) { pd = d; qf = gi; }
    }
    if (pd == VK_NULL_HANDLE) { printf("[x] 没有可用的图形队列\n"); return 4; }
    printf("[3] 选定: %s（队列族 %d）\n", dev_name(pd).c_str(), qf);

    // 先查设备到底支持哪些扩展，只启用存在的（不然 CreateDevice 会失败甚至异常）
    PFN_vkEnumerateDeviceExtensionProperties edep =
        (PFN_vkEnumerateDeviceExtensionProperties)g_gipa(inst, "vkEnumerateDeviceExtensionProperties");
    if (!edep) { printf("[x] 取不到 vkEnumerateDeviceExtensionProperties\n"); return 4; }
    uint32_t dn = 0; edep(pd, nullptr, &dn, nullptr);
    std::vector<VkExtensionProperties> dext(dn); edep(pd, nullptr, &dn, dext.data());
    auto hasDevExt = [&](const char* n) { for (auto& e : dext) if (!strcmp(e.extensionName, n)) return true; return false; };
    bool hExtMem = hasDevExt(VK_KHR_EXTERNAL_MEMORY_EXTENSION_NAME);
    bool hExtWin = hasDevExt(VK_KHR_EXTERNAL_MEMORY_WIN32_EXTENSION_NAME);
    bool hDed = hasDevExt(VK_KHR_DEDICATED_ALLOCATION_EXTENSION_NAME);
    bool hReq2 = hasDevExt(VK_KHR_GET_MEMORY_REQUIREMENTS_2_EXTENSION_NAME);
    printf("[3b] 设备扩展 %u 个: external_memory=%d external_memory_win32=%d dedicated=%d get_mem_req2=%d\n",
           dn, hExtMem, hExtWin, hDed, hReq2);
    if (!hExtWin) { printf("[x] 该设备不支持 VK_KHR_external_memory_win32，互通方案不可行\n"); return 4; }

    // ★ 直接问驱动：BGRA8 + 我的用法组合，各种句柄类型到底支持不支持
    //   （能不能外部分配取决于驱动/内存类型，先查清楚再建，别猜）
    PFN_vkGetPhysicalDeviceImageFormatProperties2 gifp2 =
        (PFN_vkGetPhysicalDeviceImageFormatProperties2)g_gipa(inst, "vkGetPhysicalDeviceImageFormatProperties2");
    VkExternalMemoryHandleTypeFlagBits tryTypes[3] = {
        VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT,
        VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_WIN32_BIT,
        VK_EXTERNAL_MEMORY_HANDLE_TYPE_OPAQUE_WIN32_KMT_BIT,
    };
    const char* typeName[3] = { "D3D11_TEXTURE(OpenSharedResource)", "OPAQUE_WIN32(NT)", "OPAQUE_WIN32_KMT" };
    bool typeOk[3] = { false, false, false };
    if (gifp2) {
        for (int i = 0; i < 3; i++) {
            VkPhysicalDeviceExternalImageFormatInfo eifi{ VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_EXTERNAL_IMAGE_FORMAT_INFO };
            eifi.handleType = tryTypes[i];
            VkPhysicalDeviceImageFormatInfo2 fifi{ VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_IMAGE_FORMAT_INFO_2 };
            fifi.pNext = &eifi;
            fifi.format = VK_FORMAT_B8G8R8A8_UNORM;
            fifi.type = VK_IMAGE_TYPE_2D;
            fifi.tiling = VK_IMAGE_TILING_OPTIMAL;
            fifi.usage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT |
                         VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_SAMPLED_BIT;
            VkExternalImageFormatProperties eifp{ VK_STRUCTURE_TYPE_EXTERNAL_IMAGE_FORMAT_PROPERTIES };
            VkImageFormatProperties2 ifp2{ VK_STRUCTURE_TYPE_IMAGE_FORMAT_PROPERTIES_2 };
            ifp2.pNext = &eifp;
            VkResult rr = gifp2(pd, &fifi, &ifp2);
            typeOk[i] = (rr == VK_SUCCESS);
            printf("[3c] %-32s → %s（externalFeatures=0x%X）\n", typeName[i],
                   typeOk[i] ? "支持 ✔" : "不支持",
                   (unsigned)eifp.externalMemoryProperties.externalMemoryFeatures);
        }
    } else {
        printf("[3c] 取不到 vkGetPhysicalDeviceImageFormatProperties2，跳过能力查询\n");
    }
    if (!typeOk[0] && !typeOk[1] && !typeOk[2]) { printf("[x] 三种句柄类型都不支持 → 这个驱动上无法做 Vulkan→D3D11 共享纹理\n"); return 4; }

    // ---------- 4. 逻辑设备（启用外部内存扩展，只启用设备确实支持的） ----------
    std::vector<const char*> devExt;
    if (hExtMem) devExt.push_back(VK_KHR_EXTERNAL_MEMORY_EXTENSION_NAME);
    devExt.push_back(VK_KHR_EXTERNAL_MEMORY_WIN32_EXTENSION_NAME);
    if (hDed)  devExt.push_back(VK_KHR_DEDICATED_ALLOCATION_EXTENSION_NAME);
    if (hReq2) devExt.push_back(VK_KHR_GET_MEMORY_REQUIREMENTS_2_EXTENSION_NAME);
    float pri = 1.0f;
    VkDeviceQueueCreateInfo qci{ VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO };
    qci.queueFamilyIndex = (uint32_t)qf; qci.queueCount = 1; qci.pQueuePriorities = &pri;
    VkDeviceCreateInfo dci{ VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO };
    dci.queueCreateInfoCount = 1; dci.pQueueCreateInfos = &qci;
    dci.enabledExtensionCount = (uint32_t)devExt.size(); dci.ppEnabledExtensionNames = devExt.data();
    printf("[4a] 即将创建逻辑设备（启用 %u 个扩展）...\n", (unsigned)devExt.size());
    VkDevice dev = VK_NULL_HANDLE;
    VkResult r = vk.CreateDevice(pd, &dci, nullptr, &dev);
    if (r != VK_SUCCESS) { printf("[x] vkCreateDevice 失败: %d（可能不支持 external_memory_win32）\n", (int)r); return 5; }
    printf("[4b] 逻辑设备创建成功\n");
    // 设备级函数：先用局部变量拿到 vkGetDeviceProcAddr，再逐个取（★ 必须在调用之前取好，
    // 否则就是空指针调用 → 0xC0000005）
    PFN_vkGetDeviceProcAddr gdpa = vk.GetDeviceProcAddr;
    if (!gdpa) { printf("[x] vkGetDeviceProcAddr 为空\n"); return 5; }
    ok = true;
    ok &= loadDev(dev, gdpa, "vkGetDeviceQueue", vk.GetDeviceQueue);
    ok &= loadDev(dev, gdpa, "vkCreateImage", vk.CreateImage);
    ok &= loadDev(dev, gdpa, "vkGetImageMemoryRequirements", vk.GetImageMemoryRequirements);
    ok &= loadDev(dev, gdpa, "vkAllocateMemory", vk.AllocateMemory);
    ok &= loadDev(dev, gdpa, "vkBindImageMemory", vk.BindImageMemory);
    ok &= loadDev(dev, gdpa, "vkCreateCommandPool", vk.CreateCommandPool);
    ok &= loadDev(dev, gdpa, "vkAllocateCommandBuffers", vk.AllocateCommandBuffers);
    ok &= loadDev(dev, gdpa, "vkBeginCommandBuffer", vk.BeginCommandBuffer);
    ok &= loadDev(dev, gdpa, "vkCmdClearColorImage", vk.CmdClearColorImage);
    ok &= loadDev(dev, gdpa, "vkEndCommandBuffer", vk.EndCommandBuffer);
    ok &= loadDev(dev, gdpa, "vkQueueSubmit", vk.QueueSubmit);
    ok &= loadDev(dev, gdpa, "vkQueueWaitIdle", vk.QueueWaitIdle);
    ok &= loadDev(dev, gdpa, "vkDestroyImage", vk.DestroyImage);
    ok &= loadDev(dev, gdpa, "vkFreeMemory", vk.FreeMemory);
    ok &= loadDev(dev, gdpa, "vkDestroyCommandPool", vk.DestroyCommandPool);
    ok &= loadDev(dev, gdpa, "vkDestroyDevice", vk.DestroyDevice);
    ok &= loadDev(dev, gdpa, "vkGetMemoryWin32HandleKHR", vk.GetMemoryWin32HandleKHR);
    if (!ok) return 5;
    VkQueue queue = VK_NULL_HANDLE; vk.GetDeviceQueue(dev, (uint32_t)qf, 0, &queue);
    if (!queue) { printf("[x] 取不到图形队列\n"); return 5; }
    printf("[4] 设备级函数就绪，图形队列已获取\n");

    // ---------- 5. 可导出的 VkImage ----------
    VkExternalMemoryImageCreateInfo extImg{ VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO };
    extImg.handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT;   // ★ KMT 句柄（D3D11 OpenSharedResource 用这个）
    VkImageCreateInfo iciImg{ VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO };
    iciImg.pNext = &extImg;
    iciImg.imageType = VK_IMAGE_TYPE_2D;
    iciImg.format = VK_FORMAT_B8G8R8A8_UNORM;      // 与 DXGI_FORMAT_B8G8R8A8_UNORM 对应
    iciImg.extent = { (uint32_t)W, (uint32_t)H, 1 };
    iciImg.mipLevels = 1; iciImg.arrayLayers = 1;
    iciImg.samples = VK_SAMPLE_COUNT_1_BIT;
    iciImg.tiling = VK_IMAGE_TILING_OPTIMAL;
    iciImg.usage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT |
                   VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_SAMPLED_BIT;
    iciImg.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    iciImg.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    VkImage img = VK_NULL_HANDLE;
    if (vk.CreateImage(dev, &iciImg, nullptr, &img) != VK_SUCCESS) { printf("[x] vkCreateImage 失败\n"); return 6; }
    VkMemoryRequirements mr{}; vk.GetImageMemoryRequirements(dev, img, &mr);

    VkExportMemoryAllocateInfo allocExport{ VK_STRUCTURE_TYPE_EXPORT_MEMORY_ALLOCATE_INFO };
    allocExport.handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT;
    VkMemoryDedicatedAllocateInfo ded{ VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO };
    ded.image = img; ded.pNext = &allocExport;
    VkMemoryAllocateInfo mai{ VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO };
    mai.pNext = &ded; mai.allocationSize = mr.size;
    mai.memoryTypeIndex = UINT32_MAX;
    // 选满足要求的显存类型
    VkPhysicalDeviceMemoryProperties mp{};
    { PFN_vkGetPhysicalDeviceMemoryProperties f = (PFN_vkGetPhysicalDeviceMemoryProperties)g_gipa(inst, "vkGetPhysicalDeviceMemoryProperties"); f(pd, &mp); }
    for (uint32_t i = 0; i < mp.memoryTypeCount; i++) {
        if ((mr.memoryTypeBits & (1u << i)) && (mp.memoryTypes[i].propertyFlags & VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT)) { mai.memoryTypeIndex = i; break; }
    }
    if (mai.memoryTypeIndex == UINT32_MAX) { printf("[x] 找不到合适的显存类型\n"); return 6; }
    VkDeviceMemory mem = VK_NULL_HANDLE;
    if (vk.AllocateMemory(dev, &mai, nullptr, &mem) != VK_SUCCESS) { printf("[x] vkAllocateMemory(导出) 失败\n"); return 6; }
    if (vk.BindImageMemory(dev, img, mem, 0) != VK_SUCCESS) { printf("[x] vkBindImageMemory 失败\n"); return 6; }

    VkMemoryGetWin32HandleInfoKHR ghi{ VK_STRUCTURE_TYPE_MEMORY_GET_WIN32_HANDLE_INFO_KHR };
    ghi.memory = mem; ghi.handleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT;
    HANDLE shared = nullptr;
    r = vk.GetMemoryWin32HandleKHR(dev, &ghi, &shared);
    if (r != VK_SUCCESS || !shared) { printf("[x] vkGetMemoryWin32HandleKHR 失败: %d\n", (int)r); return 6; }
    printf("[5] VkImage %dx%d 已导出共享 NT 句柄 = %p\n", W, H, shared);

    // ---------- 6. 清成已知颜色 ----------
    VkCommandPoolCreateInfo pci{ VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO };
    pci.queueFamilyIndex = (uint32_t)qf;
    VkCommandPool pool = VK_NULL_HANDLE;
    vk.CreateCommandPool(dev, &pci, nullptr, &pool);
    VkCommandBufferAllocateInfo cbai{ VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO };
    cbai.commandPool = pool; cbai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY; cbai.commandBufferCount = 1;
    VkCommandBuffer cb = VK_NULL_HANDLE; vk.AllocateCommandBuffers(dev, &cbai, &cb);
    VkCommandBufferBeginInfo cbbi{ VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO };
    cbbi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
    vk.BeginCommandBuffer(cb, &cbbi);
    VkImageSubresourceRange range{ VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
    // 从 UNDEFINED 到 GENERAL（清色后 D3D11 直接读，用 GENERAL 最省事）
    VkImageMemoryBarrier b{ VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER };
    b.oldLayout = VK_IMAGE_LAYOUT_UNDEFINED; b.newLayout = VK_IMAGE_LAYOUT_GENERAL;
    b.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED; b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
    b.image = img; b.subresourceRange = range;
    b.srcAccessMask = 0; b.dstAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT;
    PFN_vkCmdPipelineBarrier barrier = (PFN_vkCmdPipelineBarrier)g_gipa(inst, "vkCmdPipelineBarrier");
    barrier(cb, VK_PIPELINE_STAGE_TOP_OF_PIPE_BIT, VK_PIPELINE_STAGE_TRANSFER_BIT, 0, 0, nullptr, 0, nullptr, 1, &b);
    VkClearColorValue cc{}; cc.float32[0] = CLEAR_R; cc.float32[1] = CLEAR_G; cc.float32[2] = CLEAR_B; cc.float32[3] = CLEAR_A;
    vk.CmdClearColorImage(cb, img, VK_IMAGE_LAYOUT_GENERAL, &cc, 1, &range);
    vk.EndCommandBuffer(cb);
    VkSubmitInfo si{ VK_STRUCTURE_TYPE_SUBMIT_INFO };
    si.commandBufferCount = 1; si.pCommandBuffers = &cb;
    vk.QueueSubmit(queue, 1, &si, VK_NULL_HANDLE);
    vk.QueueWaitIdle(queue);
    printf("[6] 已清成 RGBA=(0x%02X,0x%02X,0x%02X,0xFF)\n", 0x11, 0x22, 0x33);

    // ---------- 7. D3D11 打开共享纹理并读回 ----------
    ID3D11Device* d3d = nullptr; ID3D11DeviceContext* ctx = nullptr;
    D3D_FEATURE_LEVEL fl{};
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                   nullptr, 0, D3D11_SDK_VERSION, &d3d, &fl, &ctx);
    if (FAILED(hr)) { printf("[x] D3D11CreateDevice 失败: 0x%08lX\n", (unsigned long)hr); return 7; }
    IDXGIDevice* dxdev = nullptr; d3d->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxdev);
    IDXGIAdapter* ad = nullptr; dxdev->GetAdapter(&ad);
    DXGI_ADAPTER_DESC adesc{}; ad->GetDesc(&adesc);
    printf("[7] D3D11 适配器: %ls (LUID %08lX:%08lX)\n", adesc.Description,
           (unsigned long)adesc.AdapterLuid.HighPart, (unsigned long)adesc.AdapterLuid.LowPart);

    ID3D11Device1* d3d1 = nullptr;
    if (FAILED(d3d->QueryInterface(__uuidof(ID3D11Device1), (void**)&d3d1))) { printf("[x] 拿不到 ID3D11Device1\n"); return 7; }
    ID3D11Texture2D* sharedTex = nullptr;
    // ★ KMT 句柄（Vulkan 的 D3D11_TEXTURE 类型导出）用旧 API OpenSharedResource；
    //   OpenSharedResource1 只接受 D3D11 自己 CreateSharedHandle 出来的 NT 句柄。
    hr = d3d->OpenSharedResource(shared, __uuidof(ID3D11Texture2D), (void**)&sharedTex);
    if (FAILED(hr)) {
        printf("  [i] OpenSharedResource(KMT) 失败: 0x%08lX，再试 OpenSharedResource1(NT)\n", (unsigned long)hr);
        hr = d3d1->OpenSharedResource1(shared, __uuidof(ID3D11Texture2D), (void**)&sharedTex);
    }
    if (FAILED(hr)) { printf("[x] 两种方式都打不开共享纹理: 0x%08lX\n", (unsigned long)hr); return 7; }
    D3D11_TEXTURE2D_DESC td{}; sharedTex->GetDesc(&td);
    printf("[7] OpenSharedResource1 成功: %ux%u fmt=%u bind=0x%X misc=0x%X\n",
           td.Width, td.Height, (unsigned)td.Format, td.BindFlags, td.MiscFlags);

    // staging 读回
    D3D11_TEXTURE2D_DESC sd = td;
    sd.Usage = D3D11_USAGE_STAGING; sd.BindFlags = 0; sd.CPUAccessFlags = D3D11_CPU_ACCESS_READ; sd.MiscFlags = 0;
    ID3D11Texture2D* stage = nullptr;
    hr = d3d->CreateTexture2D(&sd, nullptr, &stage);
    if (FAILED(hr)) { printf("[x] 建 staging 失败: 0x%08lX\n", (unsigned long)hr); return 7; }
    ctx->CopyResource(stage, sharedTex);
    D3D11_MAPPED_SUBRESOURCE ms{};
    hr = ctx->Map(stage, 0, D3D11_MAP_READ, 0, &ms);
    if (FAILED(hr)) { printf("[x] Map 失败: 0x%08lX\n", (unsigned long)hr); return 7; }
    const uint8_t* px = (const uint8_t*)ms.pData;
    uint8_t B = px[0], G = px[1], R = px[2], A = px[3];
    printf("[8] D3D11 读回像素(BGRA) = (%u,%u,%u,%u)  期望=(0x33,0x22,0x11,255)\n", B, G, R, A);
    bool colorOk = (R == 0x11 && G == 0x22 && B == 0x33 && A == 255);

    // 存 BMP（24 位），顺便留证据
    {
        int rowRaw = (int)ms.RowPitch, rowOut = (W * 3 + 3) & ~3;
        int dataSize = rowOut * H, fileSize = 54 + dataSize;
        std::vector<uint8_t> hdr(fileSize, 0);
        hdr[0] = 'B'; hdr[1] = 'M'; *(uint32_t*)&hdr[2] = fileSize; *(uint32_t*)&hdr[10] = 54;
        *(uint32_t*)&hdr[14] = 40; *(int32_t*)&hdr[18] = W; *(int32_t*)&hdr[22] = H;
        *(uint16_t*)&hdr[26] = 1; *(uint16_t*)&hdr[28] = 24;
        for (int y = 0; y < H; y++) {
            const uint8_t* s = px + (size_t)(H - 1 - y) * rowRaw;
            uint8_t* d = hdr.data() + 54 + (size_t)y * rowOut;
            for (int x = 0; x < W; x++) { d[x * 3 + 0] = s[x * 4 + 0]; d[x * 3 + 1] = s[x * 4 + 1]; d[x * 3 + 2] = s[x * 4 + 2]; }
        }
        FILE* f = fopen(outBmp, "wb"); if (f) { fwrite(hdr.data(), 1, hdr.size(), f); fclose(f); printf("[8] 已写出 %s（打开能看到整片颜色即互通成功）\n", outBmp); }
    }
    ctx->Unmap(stage, 0);

    // ---------- 清理 ----------
    stage->Release(); sharedTex->Release(); d3d1->Release(); ctx->Release(); d3d->Release();
    ad->Release(); dxdev->Release();
    vk.DestroyCommandPool(dev, pool, nullptr);
    vk.DestroyImage(dev, img, nullptr);
    vk.FreeMemory(dev, mem, nullptr);
    vk.DestroyDevice(dev, nullptr);
    vk.DestroyInstance(inst, nullptr);
    if (shared) CloseHandle(shared);

    printf("\n=== 结论: %s ===\n", colorOk ? "✔ Vulkan -> 共享 NT 句柄 -> D3D11 读回 颜色一致，互通可行"
                                         : "✘ 颜色不一致，需要继续排查");
    return colorOk ? 0 : 1;
}
