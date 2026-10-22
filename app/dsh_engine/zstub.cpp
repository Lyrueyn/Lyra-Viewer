// zstub.cpp —— zlib 链接桩
// Qt 的 bundled freetype/libpng 引用了 zlib 的 DLL 导入符号（__imp_z_inflate 等）。
// msyh.ttc 是 TrueType 字体，运行期根本不会走 zlib/libpng，这里只提供符号让链接通过。
// 返回错误码即可（永远不会被真正调用到）。
typedef unsigned int uInt;    // zlib 类型
typedef unsigned long uLong;

#define Z_DATA_ERROR (-3)

extern "C" {
    int z_inflate(void* s) { (void)s; return Z_DATA_ERROR; }        // 桩
    int z_inflateEnd(void* s) { (void)s; return 0; }
    int z_inflateInit2_(void* s, int w, const char* v, int n) { (void)s;(void)w;(void)v;(void)n; return 0; }
    int z_inflateReset(void* s) { (void)s; return 0; }
    int z_inflateReset2(void* s, int w) { (void)s;(void)w; return 0; }
    int z_deflate(void* s, int f) { (void)s;(void)f; return 0; }
    int z_deflateEnd(void* s) { (void)s; return 0; }
    int z_deflateInit2_(void* s, int a,int b,int c,int d,int e,const char* v,int n) { (void)s;(void)a;(void)b;(void)c;(void)d;(void)e;(void)v;(void)n; return 0; }
    int z_deflateReset(void* s) { (void)s; return 0; }
    uLong z_crc32(uLong c, const void* b, uInt n) { (void)c;(void)b;(void)n; return 0; }

    // MinGW 下 DLL 导入引用的是 __imp_z_*（指向函数的指针），这里显式提供
    void* __imp_z_inflate = (void*)&z_inflate;
    void* __imp_z_inflateEnd = (void*)&z_inflateEnd;
    void* __imp_z_inflateInit2_ = (void*)&z_inflateInit2_;
    void* __imp_z_inflateReset = (void*)&z_inflateReset;
    void* __imp_z_inflateReset2 = (void*)&z_inflateReset2;
    void* __imp_z_deflate = (void*)&z_deflate;
    void* __imp_z_deflateEnd = (void*)&z_deflateEnd;
    void* __imp_z_deflateInit2_ = (void*)&z_deflateInit2_;
    void* __imp_z_deflateReset = (void*)&z_deflateReset;
    void* __imp_z_crc32 = (void*)&z_crc32;
}
