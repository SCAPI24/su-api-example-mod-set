# 已知联机同步缺口

> 记录已确认但尚未实现/尚未验证的同步项，避免后续重复排查。

## 电路运行态同步（待实现）

当前状态：

- 客户端 `SuSubsystemElectricity` 不运行电路求解，只接收主机最终的 `CellChange`；
- 线电压、门电路内部状态、锁存器 / 计数器 / 随机发生器 / 可调延时等时序电路的中间状态没有显式同步；
- 复杂时序电路在客户端只看到最终格子变化，没有逐电路步的权威电压基线。

参考实现：

- SurvivalcraftNet：`SubsystemElectricityPackageHandler`，20 Hz 权威电路时间步 + 连接级电压基线 + 变化电压 + 移除坐标；
- 旧版 ScMultiplayer：`Func/Circuit/CircuitSynchronizer.cs`。

影响范围：

- 元件编辑/放置数据本身已同步；
- 仅影响电路运行态、中间电压和时序表现。

优先级：P1（当前未确认具体复现问题，先记录）。
