// test_danmaku_lanes.cpp — 用 DanmakuEngine 模拟真实播放，验证滚动/固定弹幕错落铺满（不堆一行）。
// g++ -std=c++17 test_danmaku_lanes.cpp danmaku_lanes.cpp -o test_danmaku_lanes.exe
#include "danmaku_lanes.h"
#include <cstdio>
#include <vector>
#include <string>
#include <cstring>
#include <map>
using namespace dmlanes;

struct T { long t; int mode; const char* text; };

int main() {
    std::vector<T> seq = {
        {1000,1,"dm1"}, {1300,1,"dm2"}, {1600,1,"dm3"},
        {2000,1,"dm4"}, {2500,1,"dm5"}, {3000,1,"dm6"},
        {3600,1,"dm7"}, {4200,1,"dm8"}, {5000,1,"dm9"},
        {5800,1,"dm10"}, {6600,1,"dm11"}, {7400,1,"dm12"},
        {8200,1,"dm13"}, {9000,1,"dm14"},
        {2000,5,"top1"}, {2600,5,"top2"}, {3200,5,"top3"},
        {2200,4,"bot1"}, {2800,4,"bot2"},
    };
    const double pw=1024, ph=600;
    DanmakuEngine eng;
    std::vector<DmInput> in;
    for (auto& t : seq) { DmInput d; d.time=t.t; d.mode=t.mode; d.color=0xFFFFFF; d.text=std::wstring(t.text, t.text+strlen(t.text)); in.push_back(d); }
    eng.SetDanmaku(in, pw, ph);
    // 逐 tick 推进
    for (long clock=0; clock<=9600; clock+=16) {
        eng.Tick(clock, pw, ph);
    }
    // 最终在飞行分布
    auto& act = eng.Actives();
    std::map<double,int> cnt; for (auto& a:act) cnt[a.y]++;
    printf("=== FINAL %zu 在飞 ===", act.size());
    printf("\n滚动/固定行分布:\n");
    for (auto& kv:cnt) printf("  y=%.0f : %d 条\n", kv.first, kv.second);
    // 打印在飞弹幕
    printf("\n--- 在飞弹幕样本 ---\n");
    for (size_t i=0;i<act.size() && i<20;i++) printf("  y=%6.1f x=%6.1f mode=%d '%s'\n", act[i].y, act[i].x, act[i].mode, act[i].text.c_str());
    return 0;
}
