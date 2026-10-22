// test_rd_offscreen.cpp —— 复刻 dsh_engine 的 draw_frame 弹幕合成(全窗口四边形+psDanmaku+预乘混合)，
// 用 FreeType 画一条"华夏风韵，洛水天依 ×3"到离屏目标，读回看是否被横向拉伸(隔离 draw_frame 逻辑)。
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <ft2build.h>
#include FT_FREETYPE_H
#include <cstdio>
#include <cstring>
#include <vector>
#include <unordered_map>
#include <cstdint>

struct QuadV { float x,y,u,v; };
struct DMGlyph { int w=0,h=0,pitch=0,adv=0,left=0,top=0; std::vector<uint8_t> buf; };

static HRESULT compile_shader(const char* src,const char* entry,const char* target,ID3DBlob** blob){
    ID3DBlob* err=nullptr; HRESULT hr=D3DCompile(src,strlen(src),nullptr,nullptr,nullptr,entry,target,0,0,blob,&err);
    if(err) err->Release(); return hr;
}
static void save_bmp(const uint8_t* px,int w,int h,const char* fname){
    int rowsz=(w*3+3)&~3; int ds=rowsz*h; std::vector<uint8_t> buf(54+ds,0);
    BITMAPFILEHEADER bf{}; bf.bfType=0x4D42; bf.bfOffBits=54; bf.bfSize=54+ds;
    BITMAPINFOHEADER bi{}; bi.biSize=40; bi.biWidth=w; bi.biHeight=h; bi.biPlanes=1; bi.biBitCount=24; bi.biSizeImage=ds;
    memcpy(buf.data(),&bf,14); memcpy(buf.data()+14,&bi,40);
    for(int y=0;y<h;y++){ const uint8_t* src=px+(size_t)(h-1-y)*w*4; uint8_t* dst=buf.data()+54+(size_t)y*rowsz;
        for(int x=0;x<w;x++){ dst[x*3+0]=src[x*4+0]; dst[x*3+1]=src[x*4+1]; dst[x*3+2]=src[x*4+2]; } }
    FILE* f=fopen(fname,"wb"); if(f){ fwrite(buf.data(),1,buf.size(),f); fclose(f); }
}

int main(){
    const int W=1525,H=939;
    ID3D11Device* dev=nullptr; ID3D11DeviceContext* ctx=nullptr;
    if(FAILED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,
        D3D11_SDK_VERSION,&dev,nullptr,&ctx))) return 1;
    // 离屏目标 (RTV+SRV)
    ID3D11Texture2D* outTex=nullptr; ID3D11RenderTargetView* outRtv=nullptr; ID3D11ShaderResourceView* outSrv=nullptr;
    { D3D11_TEXTURE2D_DESC d{}; d.Width=W; d.Height=H; d.MipLevels=1; d.ArraySize=1; d.Format=DXGI_FORMAT_B8G8R8A8_UNORM;
      d.SampleDesc.Count=1; d.Usage=D3D11_USAGE_DEFAULT; d.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
      dev->CreateTexture2D(&d,nullptr,&outTex); dev->CreateRenderTargetView(outTex,nullptr,&outRtv); dev->CreateShaderResourceView(outTex,nullptr,&outSrv); }
    // FreeType 画 "华夏风韵，洛水天依  ×3"
    FT_Library ft; FT_Face face; FT_Init_FreeType(&ft); FT_New_Face(ft,"C:\\Windows\\Fonts\\msyh.ttc",0,&face); FT_Set_Pixel_Sizes(face,0,18);
    std::vector<uint8_t> dmCpu((size_t)W*H*4,0);
    { const wchar_t* t=L"华夏风韵，洛水天依  ×3"; int asc=(int)(face->size->metrics.ascender>>6);
      int penX=1000, penY=100+asc+6; std::unordered_map<int,DMGlyph> cache;
      for(int k=0;k<(int)wcslen(t);k++){ uint32_t cp=(uint32_t)t[k]; DMGlyph* gl=nullptr; auto it=cache.find((int)cp);
        if(it!=cache.end()) gl=&it->second; else { DMGlyph ng; FT_UInt gi=FT_Get_Char_Index(face,cp);
          if(FT_Load_Glyph(face,gi,FT_LOAD_RENDER|FT_LOAD_TARGET_NORMAL)==0){ FT_Bitmap& b=face->glyph->bitmap;
            ng.adv=(int)(face->glyph->advance.x>>6); if(b.width>0&&b.rows>0&&b.buffer){ ng.w=b.width;ng.h=b.rows;ng.pitch=b.pitch;
            ng.left=face->glyph->bitmap_left; ng.top=face->glyph->bitmap_top; ng.buf.resize((size_t)ng.pitch*ng.h); memcpy(ng.buf.data(),b.buffer,(size_t)ng.pitch*ng.h);} }
          gl=&(cache[(int)cp]=std::move(ng)); }
        if(!gl) continue; int dstX=penX+gl->left, dstY=penY-gl->top;
        if(gl->w>0&&gl->h>0){ for(int ry=0;ry<gl->h;ry++){ int yy=dstY+ry; if(yy<0||yy>=H) continue;
          const uint8_t* src=gl->buf.data()+ry*gl->pitch;
          for(int rx=0;rx<gl->w;rx++){ int xx=dstX+rx; if(xx<0||xx>=W) continue; int a=src[rx]; if(a==0) continue;
            size_t idx=((size_t)yy*W+(size_t)xx)*4; dmCpu[idx+0]=(uint8_t)((0*a+127)/255); dmCpu[idx+1]=(uint8_t)((122*a+127)/255);
            dmCpu[idx+2]=(uint8_t)((179*a+127)/255); dmCpu[idx+3]=(uint8_t)a; } } }
        penX+=gl->adv;
      } }
    // 弹幕纹理 + 只读副本 + SRV
    ID3D11Texture2D* dmTex=nullptr; ID3D11ShaderResourceView* dmSrv=nullptr;
    ID3D11Texture2D* dmReadTex=nullptr; ID3D11ShaderResourceView* dmReadSrv=nullptr;
    { D3D11_TEXTURE2D_DESC d{}; d.Width=W; d.Height=H; d.MipLevels=1; d.ArraySize=1; d.Format=DXGI_FORMAT_B8G8R8A8_UNORM; d.SampleDesc.Count=1;
      d.Usage=D3D11_USAGE_DEFAULT; d.BindFlags=D3D11_BIND_SHADER_RESOURCE;
      dev->CreateTexture2D(&d,nullptr,&dmTex); dev->CreateShaderResourceView(dmTex,nullptr,&dmSrv);
      dev->CreateTexture2D(&d,nullptr,&dmReadTex); dev->CreateShaderResourceView(dmReadTex,nullptr,&dmReadSrv); }
    ctx->UpdateSubresource(dmTex,0,nullptr,dmCpu.data(),(UINT)(W*4),0);
    ctx->CopyResource(dmReadTex,dmTex);
    // 视频纹理（假蓝色 1920x1080）
    ID3D11Texture2D* vidTex=nullptr; ID3D11ShaderResourceView* vidSrv=nullptr;
    { D3D11_TEXTURE2D_DESC d{}; d.Width=1920; d.Height=1080; d.MipLevels=1; d.ArraySize=1; d.Format=DXGI_FORMAT_B8G8R8A8_UNORM; d.SampleDesc.Count=1; d.Usage=D3D11_USAGE_DEFAULT; d.BindFlags=D3D11_BIND_SHADER_RESOURCE;
      dev->CreateTexture2D(&d,nullptr,&vidTex); dev->CreateShaderResourceView(vidTex,nullptr,&vidSrv);
      std::vector<uint8_t> vpix(1920*1080*4); for(size_t i=0;i<1920*1080;i++){ vpix[i*4+0]=0; vpix[i*4+1]=0; vpix[i*4+2]=180; vpix[i*4+3]=255; } ctx->UpdateSubresource(vidTex,0,nullptr,vpix.data(),1920*4,0); }
    // 全屏四边形 vb + vs/psDanmaku + sampler + blend
    ID3D11Buffer* vb=nullptr;{ D3D11_BUFFER_DESC bd{}; bd.Usage=D3D11_USAGE_DYNAMIC; bd.ByteWidth=sizeof(QuadV)*4; bd.BindFlags=D3D11_BIND_VERTEX_BUFFER; bd.CPUAccessFlags=D3D11_CPU_ACCESS_WRITE;
      dev->CreateBuffer(&bd,nullptr,&vb); }
    ID3D11VertexShader* vs=nullptr; ID3D11PixelShader* ps=nullptr; ID3D11InputLayout* il=nullptr;
    { const char* vss="struct V{float2 p:POSITION;float2 u:TEXCOORD0;};struct O{float4 p:SV_Position;float2 u:TEXCOORD0;};O M(V i){O o;o.p=float4(i.p,0,1);o.u=i.u;return o;}";
      ID3DBlob*b=nullptr; compile_shader(vss,"M","vs_5_0",&b); dev->CreateVertexShader(b->GetBufferPointer(),b->GetBufferSize(),nullptr,&vs);
      D3D11_INPUT_ELEMENT_DESC l[2]={{"POSITION",0,DXGI_FORMAT_R32G32_FLOAT,0,0,D3D11_INPUT_PER_VERTEX_DATA,0},{"TEXCOORD",0,DXGI_FORMAT_R32G32_FLOAT,0,8,D3D11_INPUT_PER_VERTEX_DATA,0}};
      dev->CreateInputLayout(l,2,b->GetBufferPointer(),b->GetBufferSize(),&il); b->Release(); }
    { const char* pss="Texture2D tex:register(t0);SamplerState s:register(s0);float4 M(float4 p:SV_Position,float2 u:TEXCOORD0):SV_Target{return tex.Sample(s,u);}";
      ID3DBlob*b=nullptr; compile_shader(pss,"M","ps_5_0",&b); dev->CreatePixelShader(b->GetBufferPointer(),b->GetBufferSize(),nullptr,&ps); b->Release(); }
    ID3D11SamplerState* samp=nullptr;{ D3D11_SAMPLER_DESC s{}; s.Filter=D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT; s.AddressU=D3D11_TEXTURE_ADDRESS_CLAMP; s.AddressV=D3D11_TEXTURE_ADDRESS_CLAMP; s.AddressW=D3D11_TEXTURE_ADDRESS_CLAMP; dev->CreateSamplerState(&s,&samp); }
    ID3D11BlendState* bs=nullptr;{ D3D11_BLEND_DESC b{}; b.RenderTarget[0].BlendEnable=TRUE; b.RenderTarget[0].SrcBlend=D3D11_BLEND_ONE; b.RenderTarget[0].DestBlend=D3D11_BLEND_INV_SRC_ALPHA; b.RenderTarget[0].BlendOp=D3D11_BLEND_OP_ADD;
      b.RenderTarget[0].SrcBlendAlpha=D3D11_BLEND_ONE; b.RenderTarget[0].DestBlendAlpha=D3D11_BLEND_INV_SRC_ALPHA; b.RenderTarget[0].BlendOpAlpha=D3D11_BLEND_OP_ADD; b.RenderTarget[0].RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL; dev->CreateBlendState(&b,&bs); }
    // draw_frame：模拟 DLL 每帧：清目标 → 视频字母框 → 每帧重建 dmReadSrv → 弹幕 pass
    for(int fr=0; fr<500; fr++){
        float clear[4]={0,0,0,1}; ctx->ClearRenderTargetView(outRtv,clear);
        D3D11_VIEWPORT vp{0,0,(float)(W+400),(float)H,0,1}; ctx->RSSetViewports(1,&vp);   // 测试：视口比纹理宽 +400，看是否拉伸
        ctx->OMSetRenderTargets(1,&outRtv,nullptr);
        ctx->IASetInputLayout(il); UINT st=sizeof(QuadV),of=0; ctx->IASetVertexBuffers(0,1,&vb,&st,&of); ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        ctx->VSSetShader(vs,nullptr,0);
        D3D11_MAPPED_SUBRESOURCE ms;
        // 视频 pass（letterbox，假蓝色视频纹理 1920x1080）
        { float va=1920.0f/1080.0f, pa=(float)W/(float)H; float qx,qy; if(va>pa){qx=1.0f;qy=pa/va;} else {qy=1.0f;qx=va/pa;}
          QuadV vq[4]={ {-qx,qy,0,0},{qx,qy,1,0},{-qx,-qy,0,1},{qx,-qy,1,1} };
          if(SUCCEEDED(ctx->Map(vb,0,D3D11_MAP_WRITE_DISCARD,0,&ms))){ memcpy(ms.pData,vq,sizeof(vq)); ctx->Unmap(vb,0); }
          ctx->PSSetShader(ps,nullptr,0); ctx->PSSetShaderResources(0,1,&vidSrv); ctx->PSSetSamplers(0,1,&samp); ctx->OMSetBlendState(nullptr,0,0xFFFFFFFF); ctx->Draw(4,0); }
        // 每帧重传 + 重建 dmReadSrv（模拟 DLL）
        ctx->UpdateSubresource(dmTex,0,nullptr,dmCpu.data(),(UINT)(W*4),0);
        ctx->CopyResource(dmReadTex,dmTex);
        if(dmReadSrv){ dmReadSrv->Release(); dmReadSrv=nullptr; }
        dev->CreateShaderResourceView(dmReadTex,nullptr,&dmReadSrv);
        // 弹幕 pass（全窗口）+ 预乘混合
        QuadV fq[4]={ {-1,1,0,0},{1,1,1,0},{-1,-1,0,1},{1,-1,1,1} };
        if(SUCCEEDED(ctx->Map(vb,0,D3D11_MAP_WRITE_DISCARD,0,&ms))){ memcpy(ms.pData,fq,sizeof(fq)); ctx->Unmap(vb,0); }
        ctx->PSSetShader(ps,nullptr,0); ctx->PSSetShaderResources(0,1,&dmReadSrv); ctx->PSSetSamplers(0,1,&samp);
        ctx->OMSetBlendState(bs,0,0xFFFFFFFF); ctx->Draw(4,0); ctx->OMSetBlendState(nullptr,0,0xFFFFFFFF);
        ctx->OMSetRenderTargets(0,nullptr,nullptr);
    }
    // 读回 outTex 看是否拉伸
    ID3D11Texture2D* stg=nullptr;{ D3D11_TEXTURE2D_DESC d{}; d.Width=W; d.Height=H; d.MipLevels=1; d.ArraySize=1; d.Format=DXGI_FORMAT_B8G8R8A8_UNORM; d.SampleDesc.Count=1; d.Usage=D3D11_USAGE_STAGING; d.CPUAccessFlags=D3D11_CPU_ACCESS_READ; dev->CreateTexture2D(&d,nullptr,&stg); }
    ctx->CopyResource(stg,outTex);
    D3D11_MAPPED_SUBRESOURCE mr{}; std::vector<uint8_t> tight((size_t)W*H*4,0);
    if(SUCCEEDED(ctx->Map(stg,0,D3D11_MAP_READ,0,&mr))){ for(int y=0;y<H;y++) memcpy(tight.data()+(size_t)y*W*4,(uint8_t*)mr.pData+(size_t)y*mr.RowPitch,(size_t)W*4); ctx->Unmap(stg,0); }
    save_bmp(tight.data(),W,H,"test_frame.bmp");
    printf("done, saved test_frame.bmp\n");
    return 0;
}
