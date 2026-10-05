# Raid Spawn Profiler（診斷用）

2026-10-05 調查大型突襲 / DD 征服戰停格時做的量測工具，不修任何東西，平常不要開著。

## 使用方式

1. 在 `steamapps\common\RimWorld\Mods\` 執行 `git clone https://github.com/ifchen0/RaidSpawnProfiler.git`
2. 在 `Mods\RaidSpawnProfiler\Source` 執行 `dotnet build -c Release -o ../1.6/Assemblies`
3. 在 mod 清單啟用 Raid Spawn Profiler（排在最後）
4. 觸發事件後看 `Player.log` 裡的 `[RaidSpawnProfiler]`

## 會記錄的內容

- 任何 `IncidentWorker.TryExecute` 超過 100 ms：各階段的 inclusive / self 時間、呼叫次數、最大值，以及之後 5 個 tick 的耗時
- 誰呼叫了 `MapPawns.AllPawnsUnspawned`（容器掃描）的呼叫堆疊統計
- 啟動時列出其他 mod 在這些函式上的 Harmony patch
- 有裝 DeferredRaidGeneration 時：背景生成每一步的平均 / 最大耗時、GC 次數、各函式明細
- 攻城 Lord 挑到沒有技能的工兵時，記錄該 pawn

## 注意

- 掛了 70 多個 patch，會讓 pawn 生成稍微變慢，量到的數字會略高於實際值
- 要量新的函式，在 `Profiler.cs` 的 static constructor 裡用 `Add(...)` 加上即可
