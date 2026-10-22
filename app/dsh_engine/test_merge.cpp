// test_merge.cpp — 验证 DanmakuEngine 最终合并策略：**默认不合并，挤爆才合并**，且合并条数**随机有落差**。
//  g++ -std=c++17 test_merge.cpp danmaku_lanes.cpp -o test_merge.exe
//  断言：
//   [A] 不挤爆（在飞 < 行上限）+ 有重复文本 → 不合并，每条 mergeCount==1。
//   [B] 挤爆（在飞 ≥ 行上限）+ 有大量重复文本 → 触发合并；合并出的 xN **有随机落差**（不是全 x2/x3，也不是全 x13）。
//   [C] 挤爆时合并后屏幕仍尽量铺满（不因合并只剩几条而大片空白）。
#include "danmaku_lanes.h"
#include <cstdio>
#include <vector>
#include <set>
using namespace dmlanes;

int main() {
    const double pw=1024, ph=600;
    bool ok = true;

    // —— A：少量 + 重复文本，但**不挤爆** → 应 0 合并 ——
    {
        std::vector<DmInput> in;
        for (int i=0;i<8;i++){ DmInput d; d.time=1000+i*400; d.mode=1; d.color=0xFFFFFF; d.text=L"生日快乐"; in.push_back(d); }
        DanmakuEngine eng; eng.SetMerge(3000); eng.SetDanmaku(in,pw,ph);
        for (long clock=1000; clock<=1000+7*400+300; clock+=16) eng.Tick(clock,pw,ph);
        int merged=0; for (auto& a: eng.Actives()) if (a.mergeCount>1) merged++;
        printf("[A不挤爆] 在飞=%zu 合并条数=%d (expect 0)\n", eng.Actives().size(), merged);
        if (merged != 0){ printf("  FAIL: 不挤爆却合并了 %d 条\n", merged); ok=false; }
    }

    // —— B：大量重复文本，**挤爆** → 应合并，且各 xN 有随机落差 ——
    {
        std::vector<DmInput> in;
        for (int i=0;i<400;i++){ DmInput d; d.time=1000+i*70; d.mode=1; d.color=0xFFFFFF; d.text=L"生日快乐"; in.push_back(d); }
        DanmakuEngine eng; eng.SetMerge(3000); eng.SetDanmaku(in,pw,ph);
        std::set<int> counts;            // 观察到的不同 mergeCount
        int merged=0, totalSum=0;
        for (long clock=1000; clock<=1000+400*70+300; clock+=16){
            eng.Tick(clock,pw,ph);
        }
        for (auto& a: eng.Actives()){ if (a.mergeCount>1){ merged++; totalSum+=a.mergeCount; counts.insert(a.mergeCount); } }
        printf("[B挤爆] 在飞=%zu 合并条数=%d 合并计数之和=%d 不同计数种数=%zu\n",
               eng.Actives().size(), merged, totalSum, counts.size());
        for (int c : counts) printf("    出现过的 x%d\n", c);
        // 断言：确有合并
        if (merged == 0){ printf("  FAIL: 挤爆却没触发合并\n"); ok=false; }
        // 断言：计数种数应 > 1（随机落差），而不是全是同一个值（不整齐）
        if (counts.size() < 2){ printf("  FAIL: 合并计数无落差（种数=%zu，都是同一种 xN）\n", counts.size()); ok=false; }
        // 断言：不应出现 x2（最低随机下限=4，所以 2/3 不该出现）——除非确实凑不够。这里 400 条足够，应全线 x4+。
        if (counts.count(2)||counts.count(3)){ printf("  FAIL: 出现了 x2/x3（随机下限应为 4）\n"); ok=false; }
        // 覆盖行数（不空白）
        std::vector<double> ys; for (auto& a: eng.Actives()){ bool in=false; for (double y:ys) if (a.y==y){in=true;break;} if(!in) ys.push_back(a.y); }
        printf("  覆盖行数=%zu / cap %zu\n", ys.size(), (size_t)(ph/35.0));
        if (ys.size() < 10){ printf("  FAIL: 覆盖行数过少 %zu < 10（大片空白）\n", ys.size()); ok=false; }
    }
    printf("\n%s\n", ok?"PASS":"FAIL");
    return ok?0:1;
}
