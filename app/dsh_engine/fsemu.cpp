// fsemu.cpp —— 复刻 DLL 的 FreeType blit 逻辑，单独渲染"华夏风韵，洛水天依 ×3"，
// 输出文本实际横向跨度，验证是否"横向复制/铺满"。
#include <ft2build.h>
#include FT_FREETYPE_H
#include <cstdio>
#include <cstring>
#include <vector>
#include <unordered_map>
#include <cwchar>
#include <cstdint>

struct G { int w=0,h=0,pitch=0,adv=0,left=0,top=0; std::vector<unsigned char> buf; };

int main(){
    FT_Library ft=nullptr;
    if(FT_Init_FreeType(&ft)){ printf("ft init FAIL\n"); return 1; }
    FT_Face face=nullptr;
    if(FT_New_Face(ft,"C:\\Windows\\Fonts\\msyh.ttc",0,&face)){ printf("face FAIL\n"); return 1; }
    FT_Set_Pixel_Sizes(face,0,18);
    const wchar_t* t=L"华夏风韵，洛水天依  ×3";
    int asc=(int)(face->size->metrics.ascender>>6);
    int penX=1000, penY=100+asc;
    std::unordered_map<int,G> cache;
    int minX=1e9,maxX=-1e9; int count=0;
    for(int k=0;k<(int)wcslen(t);k++){
        uint32_t cp=(uint32_t)t[k];
        G* gl=nullptr; auto it=cache.find((int)cp);
        if(it!=cache.end()) gl=&it->second;
        else {
            G ng; FT_UInt gi=FT_Get_Char_Index(face,cp);
            if(FT_Load_Glyph(face,gi,FT_LOAD_RENDER|FT_LOAD_TARGET_NORMAL)==0){
                FT_Bitmap& b=face->glyph->bitmap;
                ng.adv=(int)(face->glyph->advance.x>>6);
                if(b.width>0 && b.rows>0 && b.buffer){
                    ng.w=b.width; ng.h=b.rows; ng.pitch=b.pitch;
                    ng.left=face->glyph->bitmap_left; ng.top=face->glyph->bitmap_top;
                    ng.buf.resize((size_t)ng.pitch*ng.h); memcpy(ng.buf.data(),b.buffer,(size_t)ng.pitch*ng.h);
                }
            }
            gl=&(cache[(int)cp]=std::move(ng));
        }
        int dstX=penX+gl->left;
        printf("char U+%04X  adv=%3d left=%3d w=%3d h=%3d  x=%d\n",(unsigned)cp,gl->adv,gl->left,gl->w,gl->h,dstX);
        if(gl->w>0){ if(dstX<minX)minX=dstX; if(dstX+gl->w>maxX)maxX=dstX+gl->w; }
        count++;
        penX+=gl->adv;
    }
    int span=maxX-minX;
    printf("\nchars=%d   text span x %d..%d = %d px  (naive len*18 = %d)\n",count,minX,maxX,span,(int)wcslen(t)*18);
    printf(span>700 ? ">> span 巨大 => blit 横向铺满/复制!\n" : ">> span 正常，blit 无横向复制\n");
    FT_Done_Face(face); FT_Done_FreeType(ft);
    return 0;
}
