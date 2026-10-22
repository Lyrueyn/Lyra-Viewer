// vulkan_video.h —— 当前线的 Vulkan 视频渲染器（第一版：SDR 搬运 + HDR 参数位）
//
// 架构（由 vk_probe2 实测确定）：WinUI 的 SwapChainPanel 只认 DXGI 交换链，Vulkan 不能直接 present，
// 所以 **D3D11 建渲染目标 → Vulkan 导入这块共享纹理并渲染进去 → D3D11 复制到交换链后画弹幕并 present**。
//   探针实测：D3D11 建 BGRA8 共享纹理(NT 句柄) → vkGetMemoryWin32HandleProperties OK → 导入+bind OK
//             → Vulkan 清色 → D3D11 读回颜色完全一致 ✔
//
// 全部动态加载 vulkan-1.dll（MinGW 下不依赖导入库；SDK 只用来编 SPIR-V）。
// 用 VK_KHR_dynamic_rendering（免 render pass/framebuffer 样板），apiVersion 取 1.2 保底。

#pragma once
#include <cstdio>
#include <cstdlib>
#include <cstring>

// ★ T16：大小写不敏感的短字符串比较（DSH_QUALITY 取值判断；避免依赖 _stricmp）
static inline bool qEq(const char* a, const char* b) {
    if (!a || !b) return false;
    for (; *a && *b; a++, b++) {
        char x = *a, y = *b;
        if (x >= 'A' && x <= 'Z') x += 32;
        if (y >= 'A' && y <= 'Z') y += 32;
        if (x != y) return false;
    }
    return *a == 0 && *b == 0;
}
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <vector>
#include <string>
#include <vulkan/vulkan.h>
#include "spirv_embed.h"

class VulkanVideo {
public:
    // 诊断用：最近一次失败原因
    std::string err;

    bool init() {
        if (inited) return true;
        HMODULE mod = LoadLibraryA("vulkan-1.dll");
        if (!mod) { err = "找不到 vulkan-1.dll"; return false; }
        auto gipa = (PFN_vkGetInstanceProcAddr)GetProcAddress(mod, "vkGetInstanceProcAddr");
        if (!gipa) { err = "vulkan-1.dll 无 vkGetInstanceProcAddr"; return false; }
        gi = gipa;
#define GI(name, fn) fn = (decltype(fn))gipa(nullptr, name); if (!fn) { err = "缺全局函数 " name; return false; }
        GI("vkCreateInstance", CreateInstance);
#undef GI
        const char* iext[] = { VK_KHR_EXTERNAL_MEMORY_CAPABILITIES_EXTENSION_NAME };
        VkApplicationInfo app{ VK_STRUCTURE_TYPE_APPLICATION_INFO };
        app.pApplicationName = "dsh_engine vulkan"; app.apiVersion = VK_API_VERSION_1_2;
        VkInstanceCreateInfo ici{ VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO };
        ici.pApplicationInfo = &app; ici.enabledExtensionCount = 1; ici.ppEnabledExtensionNames = iext;
        if (CreateInstance(&ici, nullptr, &inst) != VK_SUCCESS) { err = "vkCreateInstance 失败"; return false; }
#define LI(name, fn) fn = (decltype(fn))gi(inst, name); if (!fn) { err = "缺 instance 函数 " name; return false; }
        LI("vkEnumeratePhysicalDevices", EnumeratePhysicalDevices);
        LI("vkGetPhysicalDeviceProperties", GetPhysicalDeviceProperties);
        LI("vkGetPhysicalDeviceQueueFamilyProperties", GetPhysicalDeviceQueueFamilyProperties);
        LI("vkGetPhysicalDeviceMemoryProperties", GetPhysicalDeviceMemoryProperties);
        LI("vkEnumerateDeviceExtensionProperties", EnumerateDeviceExtensionProperties);
        LI("vkCreateDevice", CreateDevice);
        LI("vkGetDeviceProcAddr", GetDeviceProcAddr);
        LI("vkDestroyInstance", DestroyInstance);
        GetPhysicalDeviceImageFormatProperties2 = (PFN_vkGetPhysicalDeviceImageFormatProperties2)gi(inst, "vkGetPhysicalDeviceImageFormatProperties2");
#undef LI
        // 选独显（与 D3D11 那块纹理必须同一块卡）
        uint32_t n = 0; EnumeratePhysicalDevices(inst, &n, nullptr);
        std::vector<VkPhysicalDevice> pds(n); EnumeratePhysicalDevices(inst, &n, pds.data());
        for (auto d : pds) {
            VkPhysicalDeviceProperties p{}; GetPhysicalDeviceProperties(d, &p);
            uint32_t qn = 0; GetPhysicalDeviceQueueFamilyProperties(d, &qn, nullptr);
            std::vector<VkQueueFamilyProperties> qs(qn); GetPhysicalDeviceQueueFamilyProperties(d, &qn, qs.data());
            int f = -1;
            for (uint32_t i = 0; i < qn; i++) if (qs[i].queueFlags & VK_QUEUE_GRAPHICS_BIT) { f = (int)i; break; }
            if (f >= 0 && (phys == VK_NULL_HANDLE || p.deviceType == VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU)) {
                phys = d; qfam = f; gpuName = p.deviceName;
            }
        }
        if (phys == VK_NULL_HANDLE) { err = "没有可用的图形设备"; return false; }
        // 设备扩展：只启用存在的
        uint32_t dn = 0; EnumerateDeviceExtensionProperties(phys, nullptr, &dn, nullptr);
        std::vector<VkExtensionProperties> dext(dn); EnumerateDeviceExtensionProperties(phys, nullptr, &dn, dext.data());
        auto has = [&](const char* s) { for (auto& e : dext) if (!strcmp(e.extensionName, s)) return true; return false; };
        std::vector<const char*> ext;
        if (has(VK_KHR_EXTERNAL_MEMORY_EXTENSION_NAME)) ext.push_back(VK_KHR_EXTERNAL_MEMORY_EXTENSION_NAME);
        if (has(VK_KHR_EXTERNAL_MEMORY_WIN32_EXTENSION_NAME)) ext.push_back(VK_KHR_EXTERNAL_MEMORY_WIN32_EXTENSION_NAME);
        else { err = "设备不支持 VK_KHR_external_memory_win32"; return false; }
        if (has(VK_KHR_DEDICATED_ALLOCATION_EXTENSION_NAME)) ext.push_back(VK_KHR_DEDICATED_ALLOCATION_EXTENSION_NAME);
        if (has(VK_KHR_GET_MEMORY_REQUIREMENTS_2_EXTENSION_NAME)) ext.push_back(VK_KHR_GET_MEMORY_REQUIREMENTS_2_EXTENSION_NAME);
        if (has(VK_KHR_DYNAMIC_RENDERING_EXTENSION_NAME)) ext.push_back(VK_KHR_DYNAMIC_RENDERING_EXTENSION_NAME);
        else { err = "设备不支持 VK_KHR_dynamic_rendering"; return false; }
        VkPhysicalDeviceDynamicRenderingFeatures dr{ VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_DYNAMIC_RENDERING_FEATURES };
        dr.dynamicRendering = VK_TRUE;
        float pri = 1.0f;
        VkDeviceQueueCreateInfo qci{ VK_STRUCTURE_TYPE_DEVICE_QUEUE_CREATE_INFO };
        qci.queueFamilyIndex = (uint32_t)qfam; qci.queueCount = 1; qci.pQueuePriorities = &pri;
        VkDeviceCreateInfo dci{ VK_STRUCTURE_TYPE_DEVICE_CREATE_INFO };
        dci.pNext = &dr;
        dci.queueCreateInfoCount = 1; dci.pQueueCreateInfos = &qci;
        dci.enabledExtensionCount = (uint32_t)ext.size(); dci.ppEnabledExtensionNames = ext.data();
        if (CreateDevice(phys, &dci, nullptr, &dev) != VK_SUCCESS) { err = "vkCreateDevice 失败"; return false; }
#define LD(name, fn) fn = (decltype(fn))GetDeviceProcAddr(dev, name); if (!fn) { err = "缺设备函数 " name; return false; }
        LD("vkGetDeviceQueue", GetDeviceQueue);
        LD("vkCreateImage", CreateImage);
        LD("vkGetImageMemoryRequirements", GetImageMemoryRequirements);
        LD("vkGetImageMemoryRequirements2KHR", GetImageMemoryRequirements2KHR);
        LD("vkAllocateMemory", AllocateMemory);
        LD("vkBindImageMemory", BindImageMemory);
        LD("vkCreateImageView", CreateImageView);
        LD("vkCreateSampler", CreateSampler);
        LD("vkCreateShaderModule", CreateShaderModule);
        LD("vkCreateDescriptorSetLayout", CreateDescriptorSetLayout);
        LD("vkCreatePipelineLayout", CreatePipelineLayout);
        LD("vkCreateGraphicsPipelines", CreateGraphicsPipelines);
        LD("vkCreateDescriptorPool", CreateDescriptorPool);
        LD("vkAllocateDescriptorSets", AllocateDescriptorSets);
        LD("vkUpdateDescriptorSets", UpdateDescriptorSets);
        LD("vkCreateCommandPool", CreateCommandPool);
        LD("vkAllocateCommandBuffers", AllocateCommandBuffers);
        LD("vkBeginCommandBuffer", BeginCommandBuffer);
        LD("vkEndCommandBuffer", EndCommandBuffer);
        LD("vkCmdPipelineBarrier", CmdPipelineBarrier);
        LD("vkCmdCopyBufferToImage", CmdCopyBufferToImage);
        LD("vkCmdBeginRenderingKHR", CmdBeginRenderingKHR);
        LD("vkCmdEndRenderingKHR", CmdEndRenderingKHR);
        LD("vkCmdBindPipeline", CmdBindPipeline);
        LD("vkCmdBindDescriptorSets", CmdBindDescriptorSets);
        LD("vkCmdPushConstants", CmdPushConstants);
        LD("vkCmdDraw", CmdDraw);
        LD("vkCmdSetViewport", CmdSetViewport);
        LD("vkCmdSetScissor", CmdSetScissor);
        LD("vkCreateBuffer", CreateBuffer);
        LD("vkGetBufferMemoryRequirements", GetBufferMemoryRequirements);
        LD("vkMapMemory", MapMemory);
        LD("vkUnmapMemory", UnmapMemory);
        LD("vkQueueSubmit", QueueSubmit);
        LD("vkQueueWaitIdle", QueueWaitIdle);
        LD("vkCreateFence", CreateFence);
        LD("vkWaitForFences", WaitForFences);
        LD("vkResetFences", ResetFences);
        LD("vkDestroyFence", DestroyFence);
        LD("vkDestroyBuffer", DestroyBuffer);
        LD("vkDestroyCommandPool", DestroyCommandPool);
        LD("vkDestroyDescriptorPool", DestroyDescriptorPool);
        LD("vkDestroyPipeline", DestroyPipeline);
        LD("vkDestroyPipelineLayout", DestroyPipelineLayout);
        LD("vkDestroyDescriptorSetLayout", DestroyDescriptorSetLayout);
        LD("vkDestroyShaderModule", DestroyShaderModule);
        LD("vkDestroySampler", DestroySampler);
        LD("vkDestroyImageView", DestroyImageView);
        LD("vkDestroyImage", DestroyImage);
        LD("vkFreeMemory", FreeMemory);
        LD("vkDestroyDevice", DestroyDevice);
#undef LD
        GetMemoryWin32HandlePropertiesKHR =
            (PFN_vkGetMemoryWin32HandlePropertiesKHR)GetDeviceProcAddr(dev, "vkGetMemoryWin32HandlePropertiesKHR");
        if (!GetMemoryWin32HandlePropertiesKHR) { err = "缺 vkGetMemoryWin32HandlePropertiesKHR"; return false; }
        GetDeviceQueue(dev, (uint32_t)qfam, 0, &queue);
        VkCommandPoolCreateInfo pci{ VK_STRUCTURE_TYPE_COMMAND_POOL_CREATE_INFO };
        pci.queueFamilyIndex = (uint32_t)qfam; pci.flags = VK_COMMAND_POOL_CREATE_RESET_COMMAND_BUFFER_BIT;
        if (CreateCommandPool(dev, &pci, nullptr, &pool) != VK_SUCCESS) { err = "vkCreateCommandPool 失败"; return false; }
        VkCommandBufferAllocateInfo cbai{ VK_STRUCTURE_TYPE_COMMAND_BUFFER_ALLOCATE_INFO };
        cbai.commandPool = pool; cbai.level = VK_COMMAND_BUFFER_LEVEL_PRIMARY; cbai.commandBufferCount = 1;
        if (AllocateCommandBuffers(dev, &cbai, &cb) != VK_SUCCESS) { err = "AllocateCommandBuffers 失败"; return false; }
        VkFenceCreateInfo fci{ VK_STRUCTURE_TYPE_FENCE_CREATE_INFO };
        if (CreateFence(dev, &fci, nullptr, &fence) != VK_SUCCESS) { err = "CreateFence 失败"; return false; }
        // 着色器模块
        VkShaderModuleCreateInfo vci{ VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO };
        vci.codeSize = sizeof(kSpvVideoBlitVert); vci.pCode = kSpvVideoBlitVert;
        if (CreateShaderModule(dev, &vci, nullptr, &vsMod) != VK_SUCCESS) { err = "顶点着色器模块失败"; return false; }
        vci.codeSize = sizeof(kSpvVideoBlitFrag); vci.pCode = kSpvVideoBlitFrag;
        if (CreateShaderModule(dev, &vci, nullptr, &fsMod) != VK_SUCCESS) { err = "像素着色器模块失败"; return false; }
        // ★★ T16(A2-6)：画质滤镜（缩放带限 + 去色带/抖动）—— 环境变量 DSH_QUALITY=off|scale|deband|all
        //   · off（或任何一步失败）→ 只建原管线，走的渲染路径与改动前**逐字节相同**（可一键对比/回退）
        //   · scale/deband/all → 额外建一条用 video_blit_quality.frag 的管线
        //   失败一律回退 off + 日志，绝不因为滤镜影响播放（与 DSH_GPU 选卡同一套"失败必回退"原则）。
        { const char* e = getenv("DSH_QUALITY");
          if (!e || !e[0])          qualityMode = 3;   // 默认 all
          else if (qEq(e, "off"))   qualityMode = 0;
          else if (qEq(e, "scale")) qualityMode = 1;
          else if (qEq(e, "deband"))qualityMode = 2;
          else                      qualityMode = 3;   // all / 未识别 → all
        }
        if (qualityMode != 0) {
            VkShaderModuleCreateInfo qci{ VK_STRUCTURE_TYPE_SHADER_MODULE_CREATE_INFO };
            qci.codeSize = sizeof(kSpvVideoBlitFragQ); qci.pCode = kSpvVideoBlitFragQ;
            if (CreateShaderModule(dev, &qci, nullptr, &fsModQ) != VK_SUCCESS) {
                printf("[vk] 画质滤镜像素着色器创建失败 → 回退 off\n"); fflush(stdout);
                fsModQ = VK_NULL_HANDLE; qualityMode = 0;
            }
        }
        printf("[vk] DSH_QUALITY=%s → 画质滤镜=%s（0=off 1=scale 2=deband 3=all）\n",
               (const char*)(getenv("DSH_QUALITY") ? getenv("DSH_QUALITY") : "(未设置→默认all)"),
               qualityMode == 0 ? "off" : qualityMode == 1 ? "scale" : qualityMode == 2 ? "deband" : "all");
        fflush(stdout);
        // 采样器（线性，与 D3D11 侧 e.samp 一致）
        VkSamplerCreateInfo sci{ VK_STRUCTURE_TYPE_SAMPLER_CREATE_INFO };
        sci.magFilter = sci.minFilter = VK_FILTER_LINEAR;
        sci.mipmapMode = VK_SAMPLER_MIPMAP_MODE_NEAREST;
        sci.addressModeU = sci.addressModeV = sci.addressModeW = VK_SAMPLER_ADDRESS_MODE_CLAMP_TO_EDGE;
        sci.maxLod = 0.0f;
        if (CreateSampler(dev, &sci, nullptr, &samp) != VK_SUCCESS) { err = "CreateSampler 失败"; return false; }
        // 描述符：一个 combined image sampler
        VkDescriptorSetLayoutBinding b{};
        b.binding = 0; b.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
        b.descriptorCount = 1; b.stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT;
        VkDescriptorSetLayoutCreateInfo dslci{ VK_STRUCTURE_TYPE_DESCRIPTOR_SET_LAYOUT_CREATE_INFO };
        dslci.bindingCount = 1; dslci.pBindings = &b;
        if (CreateDescriptorSetLayout(dev, &dslci, nullptr, &dsl) != VK_SUCCESS) { err = "DescriptorSetLayout 失败"; return false; }
        VkPushConstantRange pcr{};
        pcr.stageFlags = VK_SHADER_STAGE_FRAGMENT_BIT; pcr.offset = 0; pcr.size = 48;   // 4 float + vec4 uvRect + ★T16 vec4 q（滤镜参数）
        //   ★ 原着色器只读前 32 字节；范围给 48 是安全的（大于着色器实际声明是允许的），两边共用同一个布局
        VkPipelineLayoutCreateInfo plci{ VK_STRUCTURE_TYPE_PIPELINE_LAYOUT_CREATE_INFO };
        plci.setLayoutCount = 1; plci.pSetLayouts = &dsl;
        plci.pushConstantRangeCount = 1; plci.pPushConstantRanges = &pcr;
        if (CreatePipelineLayout(dev, &plci, nullptr, &playout) != VK_SUCCESS) { err = "PipelineLayout 失败"; return false; }
        VkDescriptorPoolSize ps{ VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER, 1 };
        VkDescriptorPoolCreateInfo dpci{ VK_STRUCTURE_TYPE_DESCRIPTOR_POOL_CREATE_INFO };
        dpci.maxSets = 1; dpci.poolSizeCount = 1; dpci.pPoolSizes = &ps;
        if (CreateDescriptorPool(dev, &dpci, nullptr, &dp) != VK_SUCCESS) { err = "DescriptorPool 失败"; return false; }
        VkDescriptorSetAllocateInfo dsai{ VK_STRUCTURE_TYPE_DESCRIPTOR_SET_ALLOCATE_INFO };
        dsai.descriptorPool = dp; dsai.descriptorSetCount = 1; dsai.pSetLayouts = &dsl;
        if (AllocateDescriptorSets(dev, &dsai, &dset) != VK_SUCCESS) { err = "AllocateDescriptorSets 失败"; return false; }
        // 每帧上传用的暂存缓冲（主机可见，持久映射）
        VkBufferCreateInfo bci{ VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO };
        bci.size = 3840u * 2160u * 4u; bci.usage = VK_BUFFER_USAGE_TRANSFER_SRC_BIT;
        if (CreateBuffer(dev, &bci, nullptr, &stage) != VK_SUCCESS) { err = "CreateBuffer(staging) 失败"; return false; }
        VkMemoryRequirements bmr{}; GetBufferMemoryRequirements(dev, stage, &bmr);
        uint32_t bmt = pickMemory(bmr.memoryTypeBits,
                                  VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT);
        if (bmt == UINT32_MAX) { err = "找不到 HOST_VISIBLE 内存类型"; return false; }
        VkMemoryAllocateInfo bmai{ VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO };
        bmai.allocationSize = bmr.size; bmai.memoryTypeIndex = bmt;
        if (AllocateMemory(dev, &bmai, nullptr, &stageMem) != VK_SUCCESS) { err = "staging 内存分配失败"; return false; }
        BindBufferMemory = (PFN_vkBindBufferMemory)GetDeviceProcAddr(dev, "vkBindBufferMemory");
        if (!BindBufferMemory || BindBufferMemory(dev, stage, stageMem, 0) != VK_SUCCESS) { err = "BindBufferMemory 失败"; return false; }
        if (MapMemory(dev, stageMem, 0, VK_WHOLE_SIZE, 0, &stagePtr) != VK_SUCCESS) { err = "MapMemory 失败"; return false; }
        inited = true;
        return true;
    }

    // 绑定 D3D11 的渲染目标（NT 句柄）→ 导入成 VkImage；尺寸/格式变了会重建
    bool setTarget(void* ntHandle, int w, int h, bool hdr10) {
        if (!inited) { err = "未初始化"; return false; }
        // ★ 10bit 格式对应（实测确定）：swscale 的 X2BGR10LE 与 DXGI_FORMAT_R10G10B10A2_UNORM 配 D3D11 路径，
        //   对应到 Vulkan 就是 A2B10G10R10_UNORM_PACK32（R 在低位）—— 两者实测**帧完全一致**（YAVG 105.07 对 105.07）。
        //   ★ 反面教训：不要只看名字猜（A2R10G10B10 名字更像 D3D 的 R10G10B10A2，但实测偏暗，是错的）。
        VkFormat fmt = hdr10 ? VK_FORMAT_A2B10G10R10_UNORM_PACK32 : VK_FORMAT_B8G8R8A8_UNORM;
        if (targetImg && tw == w && th == h && tfmt == fmt) return true;
        waitIdle();
        destroyTarget();
        VkExternalMemoryImageCreateInfo ext{ VK_STRUCTURE_TYPE_EXTERNAL_MEMORY_IMAGE_CREATE_INFO };
        ext.handleTypes = VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT;
        VkImageCreateInfo ci{ VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO };
        ci.pNext = &ext;
        ci.imageType = VK_IMAGE_TYPE_2D; ci.format = fmt;
        ci.extent = { (uint32_t)w, (uint32_t)h, 1 };
        ci.mipLevels = 1; ci.arrayLayers = 1; ci.samples = VK_SAMPLE_COUNT_1_BIT;
        ci.tiling = VK_IMAGE_TILING_OPTIMAL;
        ci.usage = VK_IMAGE_USAGE_COLOR_ATTACHMENT_BIT | VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;
        ci.sharingMode = VK_SHARING_MODE_EXCLUSIVE; ci.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
        // 诊断：这个格式能不能当颜色附件 + 能不能外部分配（10bit 直通目标黑屏的排查点）
        if (GetPhysicalDeviceImageFormatProperties2) {
            VkPhysicalDeviceExternalImageFormatInfo eifi{ VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_EXTERNAL_IMAGE_FORMAT_INFO };
            eifi.handleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT;
            VkPhysicalDeviceImageFormatInfo2 fifi{ VK_STRUCTURE_TYPE_PHYSICAL_DEVICE_IMAGE_FORMAT_INFO_2 };
            fifi.pNext = &eifi; fifi.format = fmt; fifi.type = VK_IMAGE_TYPE_2D;
            fifi.tiling = VK_IMAGE_TILING_OPTIMAL; fifi.usage = ci.usage;
            VkExternalImageFormatProperties eifp{ VK_STRUCTURE_TYPE_EXTERNAL_IMAGE_FORMAT_PROPERTIES };
            VkImageFormatProperties2 ifp2{ VK_STRUCTURE_TYPE_IMAGE_FORMAT_PROPERTIES_2 };
            ifp2.pNext = &eifp;
            VkResult qr = GetPhysicalDeviceImageFormatProperties2(phys, &fifi, &ifp2);
            printf("[vk] 目标格式 %d 能力: r=%d extFeatures=0x%X maxExtent=%ux%u\n", (int)fmt, (int)qr,
                   (unsigned)eifp.externalMemoryProperties.externalMemoryFeatures,
                   ifp2.imageFormatProperties.maxExtent.width, ifp2.imageFormatProperties.maxExtent.height);
            fflush(stdout);
        }
        if (CreateImage(dev, &ci, nullptr, &targetImg) != VK_SUCCESS) { err = "导入用 vkCreateImage 失败"; return false; }
        VkMemoryRequirements mr{}; GetImageMemoryRequirements(dev, targetImg, &mr);
        VkMemoryWin32HandlePropertiesKHR hp{ VK_STRUCTURE_TYPE_MEMORY_WIN32_HANDLE_PROPERTIES_KHR };
        if (GetMemoryWin32HandlePropertiesKHR(dev, VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT, ntHandle, &hp) != VK_SUCCESS) {
            err = "vkGetMemoryWin32HandleProperties 拒绝该句柄"; return false;
        }
        VkImportMemoryWin32HandleInfoKHR imp{ VK_STRUCTURE_TYPE_IMPORT_MEMORY_WIN32_HANDLE_INFO_KHR };
        imp.handleType = VK_EXTERNAL_MEMORY_HANDLE_TYPE_D3D11_TEXTURE_BIT; imp.handle = ntHandle;
        VkMemoryDedicatedAllocateInfo ded{ VK_STRUCTURE_TYPE_MEMORY_DEDICATED_ALLOCATE_INFO };
        ded.image = targetImg; ded.pNext = &imp;
        uint32_t mt = pickMemory(mr.memoryTypeBits & hp.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
        if (mt == UINT32_MAX) mt = pickMemory(hp.memoryTypeBits, 0);
        if (mt == UINT32_MAX) { err = "没有可用内存类型导入 D3D11 纹理"; return false; }
        VkMemoryAllocateInfo mai{ VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO };
        mai.pNext = &ded; mai.allocationSize = mr.size; mai.memoryTypeIndex = mt;
        if (AllocateMemory(dev, &mai, nullptr, &targetMem) != VK_SUCCESS) { err = "导入内存分配失败"; return false; }
        if (BindImageMemory(dev, targetImg, targetMem, 0) != VK_SUCCESS) { err = "vkBindImageMemory 失败"; return false; }
        VkImageViewCreateInfo vci{ VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO };
        vci.image = targetImg; vci.viewType = VK_IMAGE_VIEW_TYPE_2D; vci.format = fmt;
        vci.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
        if (CreateImageView(dev, &vci, nullptr, &targetView) != VK_SUCCESS) { err = "target 视图失败"; return false; }
        if (!createPipeline(fmt)) return false;
        tw = w; th = h; tfmt = fmt;
        // 初始布局：UNDEFINED → GENERAL（D3D11 侧随时可读）
        if (!oneShot([&](VkCommandBuffer c) {
                transition(c, targetImg, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_GENERAL,
                           0, VK_ACCESS_MEMORY_READ_BIT | VK_ACCESS_MEMORY_WRITE_BIT);
            })) return false;
        printf("[vk] 目标已绑定: %dx%d fmt=%d (hdr10=%d)\n", w, h, (int)fmt, hdr10 ? 1 : 0);
        return true;
    }

    // 上传一帧（CPU 侧已是 BGRA8 / 10bit 打包）→ 源图
    bool uploadFrame(const void* data, int w, int h, int pitch, bool hdr10) {
        if (!inited) { err = "未初始化"; return false; }
        VkFormat fmt = hdr10 ? VK_FORMAT_A2B10G10R10_UNORM_PACK32 : VK_FORMAT_B8G8R8A8_UNORM;
        if (!srcImg || sw != w || sh != h || sfmt != fmt) {
            waitIdle(); destroySource();
            VkImageCreateInfo ci{ VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO };
            ci.imageType = VK_IMAGE_TYPE_2D; ci.format = fmt;
            ci.extent = { (uint32_t)w, (uint32_t)h, 1 };
            ci.mipLevels = 1; ci.arrayLayers = 1; ci.samples = VK_SAMPLE_COUNT_1_BIT;
            ci.tiling = VK_IMAGE_TILING_OPTIMAL;
            ci.usage = VK_IMAGE_USAGE_TRANSFER_DST_BIT | VK_IMAGE_USAGE_SAMPLED_BIT;
            ci.sharingMode = VK_SHARING_MODE_EXCLUSIVE; ci.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
            if (CreateImage(dev, &ci, nullptr, &srcImg) != VK_SUCCESS) { err = "源图创建失败"; return false; }
            VkMemoryRequirements mr{}; GetImageMemoryRequirements(dev, srcImg, &mr);
            uint32_t mt = pickMemory(mr.memoryTypeBits, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT);
            VkMemoryAllocateInfo mai{ VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO };
            mai.allocationSize = mr.size; mai.memoryTypeIndex = mt;
            if (AllocateMemory(dev, &mai, nullptr, &srcMem) != VK_SUCCESS) { err = "源图内存失败"; return false; }
            if (BindImageMemory(dev, srcImg, srcMem, 0) != VK_SUCCESS) { err = "源图绑定失败"; return false; }
            VkImageViewCreateInfo vci{ VK_STRUCTURE_TYPE_IMAGE_VIEW_CREATE_INFO };
            vci.image = srcImg; vci.viewType = VK_IMAGE_VIEW_TYPE_2D; vci.format = fmt;
            vci.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
            if (CreateImageView(dev, &vci, nullptr, &srcView) != VK_SUCCESS) { err = "源图视图失败"; return false; }
            // 描述符指向新图
            VkDescriptorImageInfo dii{}; dii.sampler = samp; dii.imageView = srcView;
            dii.imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
            VkWriteDescriptorSet wr{ VK_STRUCTURE_TYPE_WRITE_DESCRIPTOR_SET };
            wr.dstSet = dset; wr.dstBinding = 0; wr.descriptorCount = 1;
            wr.descriptorType = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER; wr.pImageInfo = &dii;
            UpdateDescriptorSets(dev, 1, &wr, 0, nullptr);
            sw = w; sh = h; sfmt = fmt;
            // 一次性转成 TRANSFER_DST
            if (!oneShot([&](VkCommandBuffer c) {
                    transition(c, srcImg, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 0, VK_ACCESS_TRANSFER_WRITE_BIT);
                })) return false;
        }
        // 主机 → 暂存缓冲
        size_t rowBytes = (size_t)w * 4;
        if (pitch <= 0) pitch = (int)rowBytes;
        if (rowBytes * h > stageSize) { err = "帧比暂存缓冲大"; return false; }
        for (int y = 0; y < h; y++) memcpy((uint8_t*)stagePtr + rowBytes * y, (const uint8_t*)data + (size_t)pitch * y, rowBytes);
        // 暂存 → 源图 → 转 SHADER_READ
        if (!oneShot([&](VkCommandBuffer c) {
                VkBufferImageCopy cp{};
                cp.bufferOffset = 0; cp.bufferRowLength = 0; cp.bufferImageHeight = 0;
                cp.imageSubresource = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 0, 1 };
                cp.imageOffset = { 0, 0, 0 };
                cp.imageExtent = { (uint32_t)w, (uint32_t)h, 1 };
                CmdCopyBufferToImage(c, stage, srcImg, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &cp);
                transition(c, srcImg, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL,
                           VK_ACCESS_TRANSFER_WRITE_BIT, VK_ACCESS_SHADER_READ_BIT);
            })) return false;
        return true;
    }

    // 渲染到绑定好的 D3D11 纹理（全屏三角形）
    // mode: 0=SDR 直搬, 1=HDR→SDR 色调映射, 2=HDR 直通(PQ 输出)
    bool render(float mode, float peak, float boost, float dither,
                float uvSx = 1.0f, float uvSy = 1.0f, float uvOx = 0.0f, float uvOy = 0.0f) {
        if (!targetImg || !srcImg) { err = "尚未绑定目标/源图"; return false; }
        // ★ T16：每个输出像素跨多少**源** texel（>1 = 缩小）。视频在目标里占 uvSx*tw × uvSy*th 个像素。
        const float destW = uvSx * (float)tw, destH = uvSy * (float)th;
        const float stepX = destW > 1.0f ? (float)sw / destW : 1.0f;
        const float stepY = destH > 1.0f ? (float)sh / destH : 1.0f;
        float pc[12] = { mode, peak, boost, dither, uvSx, uvSy, uvOx, uvOy,
                         (float)qualityMode, stepX, stepY, 0.0f };   // ★ T16: 尾部 vec4 q
        VkPipeline usePipe = (qualityMode != 0 && pipeQ) ? pipeQ : pipe;
        return oneShot([&](VkCommandBuffer c) {
            transition(c, targetImg, VK_IMAGE_LAYOUT_GENERAL, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                       VK_ACCESS_MEMORY_READ_BIT | VK_ACCESS_MEMORY_WRITE_BIT, VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT);
            VkRenderingAttachmentInfo col{ VK_STRUCTURE_TYPE_RENDERING_ATTACHMENT_INFO_KHR };
            col.imageView = targetView; col.imageLayout = VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL;
            col.loadOp = VK_ATTACHMENT_LOAD_OP_DONT_CARE; col.storeOp = VK_ATTACHMENT_STORE_OP_STORE;
            VkRenderingInfo ri{ VK_STRUCTURE_TYPE_RENDERING_INFO_KHR };
            ri.renderArea = { {0, 0}, { (uint32_t)tw, (uint32_t)th } };
            ri.layerCount = 1; ri.colorAttachmentCount = 1; ri.pColorAttachments = &col;
            CmdBeginRenderingKHR(c, &ri);
            CmdBindPipeline(c, VK_PIPELINE_BIND_POINT_GRAPHICS, usePipe);
            VkViewport vp{ 0, 0, (float)tw, (float)th, 0.0f, 1.0f };
            VkRect2D sc{ {0, 0}, { (uint32_t)tw, (uint32_t)th } };
            CmdSetViewport(c, 0, 1, &vp);
            CmdSetScissor(c, 0, 1, &sc);
            CmdBindDescriptorSets(c, VK_PIPELINE_BIND_POINT_GRAPHICS, playout, 0, 1, &dset, 0, nullptr);
            CmdPushConstants(c, playout, VK_SHADER_STAGE_FRAGMENT_BIT, 0, 48, pc);   // ★ T16: 48 字节（原着色器只读前 32）
            CmdDraw(c, 3, 1, 0, 0);
            CmdEndRenderingKHR(c);
            transition(c, targetImg, VK_IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL, VK_IMAGE_LAYOUT_GENERAL,
                       VK_ACCESS_COLOR_ATTACHMENT_WRITE_BIT, VK_ACCESS_MEMORY_READ_BIT | VK_ACCESS_MEMORY_WRITE_BIT);
        });
    }

    void waitIdle() { if (dev && QueueWaitIdle) QueueWaitIdle(queue); }
    bool ready() const { return inited && targetImg != VK_NULL_HANDLE; }
    const std::string& gpu() const { return gpuName; }
    // ★ T16：画质滤镜状态（供引擎侧日志/自测读取）
    int  qualityNow() const { return qualityMode; }
    const char* qualityName() const { return qualityMode == 0 ? "off" : qualityMode == 1 ? "scale" : qualityMode == 2 ? "deband" : "all"; }
    bool qualityActive() const { return qualityMode != 0 && pipeQ != VK_NULL_HANDLE; }

    void shutdown() {
        if (!inited) return;
        waitIdle();
        destroySource();
        destroyTarget();
        if (pipe) DestroyPipeline(dev, pipe, nullptr);
        if (playout) DestroyPipelineLayout(dev, playout, nullptr);
        if (dsl) DestroyDescriptorSetLayout(dev, dsl, nullptr);
        if (dp) DestroyDescriptorPool(dev, dp, nullptr);
        if (vsMod) DestroyShaderModule(dev, vsMod, nullptr);
        if (fsMod) DestroyShaderModule(dev, fsMod, nullptr);
        if (fsModQ) DestroyShaderModule(dev, fsModQ, nullptr);
        if (pipeQ) DestroyPipeline(dev, pipeQ, nullptr);
        if (samp) DestroySampler(dev, samp, nullptr);
        if (fence) DestroyFence(dev, fence, nullptr);
        if (stage) DestroyBuffer(dev, stage, nullptr);
        if (stageMem) FreeMemory(dev, stageMem, nullptr);
        if (pool) DestroyCommandPool(dev, pool, nullptr);
        if (dev) DestroyDevice(dev, nullptr);
        if (inst) DestroyInstance(inst, nullptr);
        // ★ 关键：所有成员必须复位成初始值。否则"关掉再启用"（切直通后再切回 SDR 就会走这条路）
        //   会带着死句柄/悬空指针继续跑 —— 尤其 stagePtr 指向已释放的映射内存，下一帧 memcpy 直接崩。
        pipe = VK_NULL_HANDLE; playout = VK_NULL_HANDLE; dsl = VK_NULL_HANDLE; dp = VK_NULL_HANDLE;
        dset = VK_NULL_HANDLE; vsMod = VK_NULL_HANDLE; fsMod = VK_NULL_HANDLE; samp = VK_NULL_HANDLE;
        fsModQ = VK_NULL_HANDLE; pipeQ = VK_NULL_HANDLE;
        fence = VK_NULL_HANDLE; pool = VK_NULL_HANDLE; cb = VK_NULL_HANDLE;
        stage = VK_NULL_HANDLE; stageMem = VK_NULL_HANDLE; stagePtr = nullptr;
        dev = VK_NULL_HANDLE; inst = VK_NULL_HANDLE; phys = VK_NULL_HANDLE; queue = VK_NULL_HANDLE;
        qfam = -1; gpuName.clear();
        tw = th = sw = sh = 0; tfmt = sfmt = VK_FORMAT_UNDEFINED;
        gi = nullptr;
        CreateInstance = nullptr; EnumeratePhysicalDevices = nullptr; GetPhysicalDeviceProperties = nullptr;
        GetPhysicalDeviceQueueFamilyProperties = nullptr; GetPhysicalDeviceMemoryProperties = nullptr;
        EnumerateDeviceExtensionProperties = nullptr; CreateDevice = nullptr; GetDeviceProcAddr = nullptr;
        DestroyInstance = nullptr; GetDeviceQueue = nullptr; CreateImage = nullptr;
        GetImageMemoryRequirements = nullptr; GetImageMemoryRequirements2KHR = nullptr;
        AllocateMemory = nullptr; BindImageMemory = nullptr; BindBufferMemory = nullptr;
        CreateImageView = nullptr; CreateSampler = nullptr; CreateShaderModule = nullptr;
        CreateDescriptorSetLayout = nullptr; CreatePipelineLayout = nullptr; CreateGraphicsPipelines = nullptr;
        CreateDescriptorPool = nullptr; AllocateDescriptorSets = nullptr; UpdateDescriptorSets = nullptr;
        CreateCommandPool = nullptr; AllocateCommandBuffers = nullptr; BeginCommandBuffer = nullptr;
        EndCommandBuffer = nullptr; CmdPipelineBarrier = nullptr; CmdCopyBufferToImage = nullptr;
        CmdBeginRenderingKHR = nullptr; CmdEndRenderingKHR = nullptr; CmdBindPipeline = nullptr;
        CmdBindDescriptorSets = nullptr; CmdPushConstants = nullptr; CmdDraw = nullptr;
        CmdSetViewport = nullptr; CmdSetScissor = nullptr; CreateBuffer = nullptr;
        GetBufferMemoryRequirements = nullptr; MapMemory = nullptr; UnmapMemory = nullptr;
        QueueSubmit = nullptr; QueueWaitIdle = nullptr; CreateFence = nullptr; WaitForFences = nullptr;
        ResetFences = nullptr; DestroyFence = nullptr; DestroyBuffer = nullptr;
        DestroyCommandPool = nullptr; DestroyDescriptorPool = nullptr; DestroyPipeline = nullptr;
        DestroyPipelineLayout = nullptr; DestroyDescriptorSetLayout = nullptr;
        DestroyShaderModule = nullptr; DestroySampler = nullptr; DestroyImageView = nullptr;
        DestroyImage = nullptr; FreeMemory = nullptr; DestroyDevice = nullptr;
        GetMemoryWin32HandlePropertiesKHR = nullptr; GetPhysicalDeviceImageFormatProperties2 = nullptr;
        inited = false;
    }

private:
    uint32_t pickMemory(uint32_t bits, VkMemoryPropertyFlags want) {
        VkPhysicalDeviceMemoryProperties mp{};
        GetPhysicalDeviceMemoryProperties(phys, &mp);
        for (uint32_t i = 0; i < mp.memoryTypeCount; i++)
            if ((bits & (1u << i)) && (mp.memoryTypes[i].propertyFlags & want) == want) return i;
        return UINT32_MAX;
    }

    void transition(VkCommandBuffer c, VkImage img, VkImageLayout from, VkImageLayout to,
                    VkAccessFlags srcA, VkAccessFlags dstA) {
        VkImageMemoryBarrier b{ VK_STRUCTURE_TYPE_IMAGE_MEMORY_BARRIER };
        b.oldLayout = from; b.newLayout = to;
        b.srcQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED; b.dstQueueFamilyIndex = VK_QUEUE_FAMILY_IGNORED;
        b.image = img; b.subresourceRange = { VK_IMAGE_ASPECT_COLOR_BIT, 0, 1, 0, 1 };
        b.srcAccessMask = srcA; b.dstAccessMask = dstA;
        CmdPipelineBarrier(c, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT, VK_PIPELINE_STAGE_ALL_COMMANDS_BIT,
                           0, 0, nullptr, 0, nullptr, 1, &b);
    }

    // 起一个一次性命令缓冲 → 提交 → 等完成（v1 用同步交换，简单可靠）
    template <typename F> bool oneShot(F body) {
        VkCommandBufferBeginInfo bi{ VK_STRUCTURE_TYPE_COMMAND_BUFFER_BEGIN_INFO };
        bi.flags = VK_COMMAND_BUFFER_USAGE_ONE_TIME_SUBMIT_BIT;
        if (BeginCommandBuffer(cb, &bi) != VK_SUCCESS) { err = "BeginCommandBuffer 失败"; return false; }
        body(cb);
        if (EndCommandBuffer(cb) != VK_SUCCESS) { err = "EndCommandBuffer 失败"; return false; }
        VkSubmitInfo si{ VK_STRUCTURE_TYPE_SUBMIT_INFO };
        si.commandBufferCount = 1; si.pCommandBuffers = &cb;
        if (QueueSubmit(queue, 1, &si, fence) != VK_SUCCESS) { err = "QueueSubmit 失败"; return false; }
        if (WaitForFences(dev, 1, &fence, VK_TRUE, 5000000000ull) != VK_SUCCESS) { err = "等 fence 超时"; return false; }
        ResetFences(dev, 1, &fence);
        return true;
    }

    bool createPipeline(VkFormat colorFmt) {
        if (pipe) { DestroyPipeline(dev, pipe, nullptr); pipe = VK_NULL_HANDLE; }
        if (pipeQ) { DestroyPipeline(dev, pipeQ, nullptr); pipeQ = VK_NULL_HANDLE; }
        if (!buildPipeline(fsMod, colorFmt, &pipe)) return false;
        // ★ T16：滤镜管线（失败 → 回退 off，本帧起继续用原管线）
        if (qualityMode != 0 && fsModQ) {
            if (!buildPipeline(fsModQ, colorFmt, &pipeQ)) {
                printf("[vk] 画质滤镜管线创建失败 → 回退 off\n"); fflush(stdout);
                qualityMode = 0; pipeQ = VK_NULL_HANDLE;
            } else {
                printf("[vk] 画质滤镜管线已启用（%s）\n",
                       qualityMode == 1 ? "scale" : qualityMode == 2 ? "deband" : "all");
                fflush(stdout);
            }
        }
        return true;
    }

    bool buildPipeline(VkShaderModule frag, VkFormat colorFmt, VkPipeline* out) {
        VkPipelineShaderStageCreateInfo st[2]{};
        st[0].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
        st[0].stage = VK_SHADER_STAGE_VERTEX_BIT; st[0].module = vsMod; st[0].pName = "main";
        st[1].sType = VK_STRUCTURE_TYPE_PIPELINE_SHADER_STAGE_CREATE_INFO;
        st[1].stage = VK_SHADER_STAGE_FRAGMENT_BIT; st[1].module = frag; st[1].pName = "main";
        VkPipelineVertexInputStateCreateInfo vi{ VK_STRUCTURE_TYPE_PIPELINE_VERTEX_INPUT_STATE_CREATE_INFO };
        VkPipelineInputAssemblyStateCreateInfo ia{ VK_STRUCTURE_TYPE_PIPELINE_INPUT_ASSEMBLY_STATE_CREATE_INFO };
        ia.topology = VK_PRIMITIVE_TOPOLOGY_TRIANGLE_LIST;
        VkPipelineViewportStateCreateInfo vps{ VK_STRUCTURE_TYPE_PIPELINE_VIEWPORT_STATE_CREATE_INFO };
        vps.viewportCount = 1; vps.scissorCount = 1;
        // ★ 视口/剪裁声明为"动态"（在命令缓冲里用 vkCmdSetViewport/Scissor 设置）：
        //   否则 pViewports/pScissors 为 null 且计数为 1 —— 驱动在 CreateGraphicsPipelines 里会直接崩。
        VkDynamicState dyn[2] = { VK_DYNAMIC_STATE_VIEWPORT, VK_DYNAMIC_STATE_SCISSOR };
        VkPipelineDynamicStateCreateInfo ds{ VK_STRUCTURE_TYPE_PIPELINE_DYNAMIC_STATE_CREATE_INFO };
        ds.dynamicStateCount = 2; ds.pDynamicStates = dyn;
        VkPipelineRasterizationStateCreateInfo rs{ VK_STRUCTURE_TYPE_PIPELINE_RASTERIZATION_STATE_CREATE_INFO };
        rs.polygonMode = VK_POLYGON_MODE_FILL; rs.cullMode = VK_CULL_MODE_NONE;
        rs.frontFace = VK_FRONT_FACE_COUNTER_CLOCKWISE; rs.lineWidth = 1.0f;
        VkPipelineMultisampleStateCreateInfo ms{ VK_STRUCTURE_TYPE_PIPELINE_MULTISAMPLE_STATE_CREATE_INFO };
        ms.rasterizationSamples = VK_SAMPLE_COUNT_1_BIT;
        VkPipelineColorBlendAttachmentState cba{};
        cba.colorWriteMask = 0xF; cba.blendEnable = VK_FALSE;
        VkPipelineColorBlendStateCreateInfo cbs{ VK_STRUCTURE_TYPE_PIPELINE_COLOR_BLEND_STATE_CREATE_INFO };
        cbs.attachmentCount = 1; cbs.pAttachments = &cba;
        VkPipelineLayout pl = playout;
        VkGraphicsPipelineCreateInfo gp{ VK_STRUCTURE_TYPE_GRAPHICS_PIPELINE_CREATE_INFO };
        gp.stageCount = 2; gp.pStages = st;
        gp.pVertexInputState = &vi; gp.pInputAssemblyState = &ia;
        gp.pViewportState = &vps; gp.pRasterizationState = &rs; gp.pMultisampleState = &ms;
        gp.pColorBlendState = &cbs; gp.layout = pl;
        gp.pDynamicState = &ds;
        // 动态渲染：管线里要声明颜色附件格式
        VkPipelineRenderingCreateInfo prc{ VK_STRUCTURE_TYPE_PIPELINE_RENDERING_CREATE_INFO_KHR };
        prc.colorAttachmentCount = 1; prc.pColorAttachmentFormats = &colorFmt;
        gp.pNext = &prc;
        if (CreateGraphicsPipelines(dev, VK_NULL_HANDLE, 1, &gp, nullptr, out) != VK_SUCCESS) {
            err = "CreateGraphicsPipelines 失败"; return false;
        }
        return true;
    }

    void destroyTarget() {
        if (targetView) { DestroyImageView(dev, targetView, nullptr); targetView = VK_NULL_HANDLE; }
        if (targetImg) { DestroyImage(dev, targetImg, nullptr); targetImg = VK_NULL_HANDLE; }
        if (targetMem) { FreeMemory(dev, targetMem, nullptr); targetMem = VK_NULL_HANDLE; }
        tw = th = 0; tfmt = VK_FORMAT_UNDEFINED;
    }
    void destroySource() {
        if (srcView) { DestroyImageView(dev, srcView, nullptr); srcView = VK_NULL_HANDLE; }
        if (srcImg) { DestroyImage(dev, srcImg, nullptr); srcImg = VK_NULL_HANDLE; }
        if (srcMem) { FreeMemory(dev, srcMem, nullptr); srcMem = VK_NULL_HANDLE; }
        sw = sh = 0; sfmt = VK_FORMAT_UNDEFINED;
    }

    bool inited = false;
    PFN_vkGetInstanceProcAddr gi = nullptr;
    VkInstance inst = VK_NULL_HANDLE;
    VkPhysicalDevice phys = VK_NULL_HANDLE;
    VkDevice dev = VK_NULL_HANDLE;
    VkQueue queue = VK_NULL_HANDLE;
    int qfam = -1;
    std::string gpuName;
    VkCommandPool pool = VK_NULL_HANDLE;
    VkCommandBuffer cb = VK_NULL_HANDLE;
    VkFence fence = VK_NULL_HANDLE;
    VkShaderModule vsMod = VK_NULL_HANDLE, fsMod = VK_NULL_HANDLE;
    VkShaderModule fsModQ = VK_NULL_HANDLE;        // ★ T16 画质滤镜版像素着色器
    VkPipeline pipeQ = VK_NULL_HANDLE;             // ★ T16 画质滤镜管线
    int qualityMode = 0;                           // ★ T16 0=off 1=scale 2=deband 3=all
    VkSampler samp = VK_NULL_HANDLE;
    VkDescriptorSetLayout dsl = VK_NULL_HANDLE;
    VkPipelineLayout playout = VK_NULL_HANDLE;
    VkPipeline pipe = VK_NULL_HANDLE;
    VkDescriptorPool dp = VK_NULL_HANDLE;
    VkDescriptorSet dset = VK_NULL_HANDLE;
    VkImage targetImg = VK_NULL_HANDLE, srcImg = VK_NULL_HANDLE;
    VkDeviceMemory targetMem = VK_NULL_HANDLE, srcMem = VK_NULL_HANDLE;
    VkImageView targetView = VK_NULL_HANDLE, srcView = VK_NULL_HANDLE;
    int tw = 0, th = 0, sw = 0, sh = 0;
    VkFormat tfmt = VK_FORMAT_UNDEFINED, sfmt = VK_FORMAT_UNDEFINED;
    VkBuffer stage = VK_NULL_HANDLE;
    VkDeviceMemory stageMem = VK_NULL_HANDLE;
    void* stagePtr = nullptr;
    VkDeviceSize stageSize = 3840ull * 2160ull * 4ull;

    // 函数指针
    PFN_vkCreateInstance CreateInstance = nullptr;
    PFN_vkEnumeratePhysicalDevices EnumeratePhysicalDevices = nullptr;
    PFN_vkGetPhysicalDeviceProperties GetPhysicalDeviceProperties = nullptr;
    PFN_vkGetPhysicalDeviceQueueFamilyProperties GetPhysicalDeviceQueueFamilyProperties = nullptr;
    PFN_vkGetPhysicalDeviceMemoryProperties GetPhysicalDeviceMemoryProperties = nullptr;
    PFN_vkEnumerateDeviceExtensionProperties EnumerateDeviceExtensionProperties = nullptr;
    PFN_vkCreateDevice CreateDevice = nullptr;
    PFN_vkGetDeviceProcAddr GetDeviceProcAddr = nullptr;
    PFN_vkDestroyInstance DestroyInstance = nullptr;
    PFN_vkGetDeviceQueue GetDeviceQueue = nullptr;
    PFN_vkCreateImage CreateImage = nullptr;
    PFN_vkGetImageMemoryRequirements GetImageMemoryRequirements = nullptr;
    PFN_vkGetImageMemoryRequirements2KHR GetImageMemoryRequirements2KHR = nullptr;
    PFN_vkAllocateMemory AllocateMemory = nullptr;
    PFN_vkBindImageMemory BindImageMemory = nullptr;
    PFN_vkBindBufferMemory BindBufferMemory = nullptr;
    PFN_vkCreateImageView CreateImageView = nullptr;
    PFN_vkCreateSampler CreateSampler = nullptr;
    PFN_vkCreateShaderModule CreateShaderModule = nullptr;
    PFN_vkCreateDescriptorSetLayout CreateDescriptorSetLayout = nullptr;
    PFN_vkCreatePipelineLayout CreatePipelineLayout = nullptr;
    PFN_vkCreateGraphicsPipelines CreateGraphicsPipelines = nullptr;
    PFN_vkCreateDescriptorPool CreateDescriptorPool = nullptr;
    PFN_vkAllocateDescriptorSets AllocateDescriptorSets = nullptr;
    PFN_vkUpdateDescriptorSets UpdateDescriptorSets = nullptr;
    PFN_vkCreateCommandPool CreateCommandPool = nullptr;
    PFN_vkAllocateCommandBuffers AllocateCommandBuffers = nullptr;
    PFN_vkBeginCommandBuffer BeginCommandBuffer = nullptr;
    PFN_vkEndCommandBuffer EndCommandBuffer = nullptr;
    PFN_vkCmdPipelineBarrier CmdPipelineBarrier = nullptr;
    PFN_vkCmdCopyBufferToImage CmdCopyBufferToImage = nullptr;
    PFN_vkCmdBeginRenderingKHR CmdBeginRenderingKHR = nullptr;
    PFN_vkCmdEndRenderingKHR CmdEndRenderingKHR = nullptr;
    PFN_vkCmdBindPipeline CmdBindPipeline = nullptr;
    PFN_vkCmdBindDescriptorSets CmdBindDescriptorSets = nullptr;
    PFN_vkCmdPushConstants CmdPushConstants = nullptr;
    PFN_vkCmdDraw CmdDraw = nullptr;
    PFN_vkCmdSetViewport CmdSetViewport = nullptr;
    PFN_vkCmdSetScissor CmdSetScissor = nullptr;
    PFN_vkCreateBuffer CreateBuffer = nullptr;
    PFN_vkGetBufferMemoryRequirements GetBufferMemoryRequirements = nullptr;
    PFN_vkMapMemory MapMemory = nullptr;
    PFN_vkUnmapMemory UnmapMemory = nullptr;
    PFN_vkQueueSubmit QueueSubmit = nullptr;
    PFN_vkQueueWaitIdle QueueWaitIdle = nullptr;
    PFN_vkCreateFence CreateFence = nullptr;
    PFN_vkWaitForFences WaitForFences = nullptr;
    PFN_vkResetFences ResetFences = nullptr;
    PFN_vkDestroyFence DestroyFence = nullptr;
    PFN_vkDestroyBuffer DestroyBuffer = nullptr;
    PFN_vkDestroyCommandPool DestroyCommandPool = nullptr;
    PFN_vkDestroyDescriptorPool DestroyDescriptorPool = nullptr;
    PFN_vkDestroyPipeline DestroyPipeline = nullptr;
    PFN_vkDestroyPipelineLayout DestroyPipelineLayout = nullptr;
    PFN_vkDestroyDescriptorSetLayout DestroyDescriptorSetLayout = nullptr;
    PFN_vkDestroyShaderModule DestroyShaderModule = nullptr;
    PFN_vkDestroySampler DestroySampler = nullptr;
    PFN_vkDestroyImageView DestroyImageView = nullptr;
    PFN_vkDestroyImage DestroyImage = nullptr;
    PFN_vkFreeMemory FreeMemory = nullptr;
    PFN_vkDestroyDevice DestroyDevice = nullptr;
    PFN_vkGetMemoryWin32HandlePropertiesKHR GetMemoryWin32HandlePropertiesKHR = nullptr;
    PFN_vkGetPhysicalDeviceImageFormatProperties2 GetPhysicalDeviceImageFormatProperties2 = nullptr;
};
