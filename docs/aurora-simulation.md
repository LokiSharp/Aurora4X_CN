# Aurora 怎么做模拟

这份说明给做太空兵棋用。依据是本机 `C:\Game\Aurora\AuroraDB.db` 的表和已经算好的组件数值，加上这些数之间能直接验算的关系。没有把游戏的 C# 逐行拆出来。运行时的判定细节如果表里看不出，会标明是结构推断，不是从代码里抄出的公式。

Aurora 是一款单人 4X。对兵棋有用的不是它的界面，而是它把「设计」「命令」「接触」「真实单位」拆开，并且用两种时间节拍推进。

## 程序形状

游戏是 32 位 .NET Framework 4.8 的 WinForms 程序。权威状态几乎全在 SQLite 库 `AuroraDB.db` 里。界面负责下订单、改设计、把库里的数画出来。点一次「前进」时，程序按时间增量读库、改坐标和库存、把结果写回去。

表名有两套前缀：

- `DIM_` 是规则和名字素材，换一局游戏大体不变。这版有 54 张。
- `FCT_` 是这一局的事实：舰、舰队、殖民地、接触、订单、损伤。这版有 150 张。

一局游戏用 `GameID` 分开。几乎每张事实表都带 `GameID`。种族用 `RaceID`。所以玩家、非玩家、蜂群、入侵者走的是同一套表，不是另一套战斗程序。

## 时间

`FCT_Game.GameTime` 是全局时钟，单位是秒。这版新游戏的值是：

| 字段 | 值 | 含义 |
| --- | --- | --- |
| SubPulseLength | 5 | 战斗子脉冲，秒 |
| OneSecondSubPulse | 1 | 允许把移动再切成 1 秒 |
| MinConstructionPeriod | 430000 | 建设节拍，约 4.98 天 |
| MinGroundCombatPeriod | 28800 | 地面战节拍，8 小时 |
| ResearchSpeed 等 | 100 | 科研、改造、勘测的速度系数，100 为基准 |

`FCT_Increments` 记下每次实际推进了多长。舰船的 `LastMoveTime`、导弹的 `LaunchTime`、接触的 `LastUpdate` 都在这条秒轴上。

兵棋可以学的分法：交战用短节拍，造船、采矿、人口用长节拍。不要让经济公式每个战斗秒都跑一遍。Aurora 用 `LastGrowthTime`、`LastGroundCombatTime` 记住上次长节拍跑到哪。

## 空间

一个星系是一张平面，坐标 `Xcor`、`Ycor`，单位是千米。星系之间不共面，只能通过跃迁点相连。

- `FCT_System` 是星系。尘埃密度、年龄、丰度修正放在这里。
- `FCT_JumpPoint` 是边上的门。有所属星系、到恒星的距离和方位、自己的坐标，以及 `WPLink` 指向另一侧。可以有跃迁门强度。
- `FCT_SystemBody` 是恒星、行星、卫星、小行星。质量、半径、重力、轨道半长轴、偏心率、公转周期、表面温度、大气压都存着。`OrbitalMotion` 打开时，程序按时间重算天体位置，而不是只存一个固定点。
- `FCT_Waypoint` 是某种族自己的航点，可以钉在天体、跃迁点、拉格朗日点或深空坐标上。
- `FCT_LagrangePoint`、`FCT_AetherGates`、`Wormholes` 是另外几种可到达的点。

舰队位置在 `FCT_Fleet`：`SystemID`、`Xcor`、`Ycor`，外加 `LastXcor`、`LastYcor`、`IncrementStartX`、`IncrementStartY`。后四个说明每个增量里，舰队被当成一段直线运动：增量开始记起点，结束记终点。接触表和导弹齐射表也有同一组字段。插值和「这一拍刚移动过没有」都靠这组数，不靠保存整条轨迹。

## 命令

移动不是每拍重选方向。玩家或 AI 往 `FCT_MoveOrders` 里压一条订单，字段包括：

- 做什么：`MoveActionID`
- 去哪：目标星系、目标类型、目标 ID、人口、天体或跃迁点
- 约束：最大数量、最小距离、延迟、需要的勘测点、需要的时间
- 状态：`Arrived`、`OrderDelayRemaining`

`DIM_MoveAction` 这版有 117 种动作。每种动作自己声明需要哪种目标（天体、跃迁点、舰队、接触、残骸、救生舱、拉格朗日点、以太门、深空），以及它是过境、静止作业，还是普通移动。例子：标准过境、移动到坐标、跟随、冲撞、扩展轨道、加入舰队、救援幸存者。

舰队上还有两套不进当前队列、但每拍都检查的命令：常备命令和条件命令（`FCT_FleetStandingOrder`、`FCT_FleetConditionalOrder`）。条件是「燃料低于、接触出现、受到伤害」这一类，对应动作仍然是那 117 种之一。

这是最值得拿走的结构。玩家点的按钮和 AI 下的决定，最后都变成同一张订单表。战斗数学不关心命令是谁下的。

## 舰船的四层

不要把「科技等级」直接拿去参与每一发命中。Aurora 分成四层，越往下越是运行时对象。

1. `FCT_TechSystem`：科技节点。有类别、前置、研发费用、是否常规科技、是否遗迹专属。数值参数放在 `AdditionalInfo` 到 `AdditionalInfo4`。
2. `FCT_ShipDesignComponents`：已经设计好的一件设备。功率、跟踪、伤害、射程系数、充能、燃料效率、命中修正、穿甲、HTK、矿物成本都写死在这一行。运行时不再回科技树重算。
3. `FCT_ShipClass`：一个舰级。把组件收成舰级速度、燃料效率、截面、热信号、电磁信号、装甲、护盾、传感器强度、货舱、机库、跃迁距离。
4. `FCT_Ship`：一艘具体的船。油、护盾当前值、船员、士气、维护补给、损伤、上次开火、上次被命中。同级船这些数不同。

实测，军舰速度满足：

`MaxSpeed = EnginePower / Size × 1000`

`Size` 是船体 HS。1 HS = 50 吨，所以速度是「总引擎功率 / 船体吨位」再乘一个常数。天鹅级样本：功率 18869.76，船体 965.855 HS，速度 19536 km/s，与公式一致。燃料效率是舰级上的一个系数，航行时用它把距离换成燃料。

组件类型这版有 58 种，都在 `DIM_ComponentType`。引擎、乘员舱、燃料、货舱、弹仓、两种勘测、热传感器、指挥、装甲、护盾、激光、跃迁引擎、反应堆、束射火控、电子战、导弹发射架、主动传感器、导弹火控、粒子束、导轨炮、等离子炮、高斯炮、微波、近防、隐形、维修、诱饵发射架，都是类型，不是写死的船。

## 一次设计在存什么

激光这一行能看出设计器的输出，而不是运行时临时算的：

| 名字 | 伤害 | 射程系数 | 充能 | HTK | 体积 HS |
| --- | --- | --- | --- | --- | --- |
| 10cm C1.5 红外 | 3 | 10000 | 1.5 | 1 | 3 |
| 10cm C3 紫外 | 3 | 40000 | 3 | 1 | 3 |
| 30cm C6 软 X | 24 | 60000 | 6 | 5 | 10 |

伤害列是 `DamageOutput`。射程系数是 `RangeModifier`，红外 10000、紫外 40000、软 X 射线 60000，这是波长，不是已经算好的最大公里数。最大射程是伤害和波长系数的乘积，距离越远伤害按档下降。充能时间是 `RechargeRate`，单位是秒这一类的节拍，不是「每分钟射几发」。

引擎行分开存三件事：`ComponentValue` 是输出功率，`EnginePowerMod` 是相对标准尺寸的功率倍数，`FuelEfficiency` 是耗油系数。民用引擎功率倍数是 0.4 这一档，耗油系数小一个数量级，所以慢但省油。舰级不再保存每个引擎，只保存加总后的 `EnginePower` 和合并后的 `FuelEfficiency`。

传感器在组件上保存强度、分辨率、最大距离、跟踪速度。舰级再保存全舰的主动强度、被动热强度、电磁强度、截面 `ClassCrossSection`、热信号 `ClassThermal`。探测时用的是这些汇总数，加上目标舰级的截面和信号。

## 探测和接触

真实船在 `FCT_Ship`。你看见的东西在 `FCT_Contacts`。两者不是同一行。

接触记下：谁探测到的、对方种族、接触方式、强度、分辨率、坐标、速度、连续接触了多久、这一增量的起点坐标。接触可以中断再建立（`Reestablished`）。对方的舰级如果只是估计，写进 `FCT_AlienClass`，下面再挂估计的传感器、武器和科技，不回写真实舰级。

所以兵棋里的战争迷雾应该是一张独立的接触表。真实坐标每拍都在变。接触坐标可以外推，也可以过期。火控锁定的是接触或真实 ID 加类型（`FCT_FireControlAssignment` 的 `TargetID` 和 `TargetType`），不是一个永远可见的单位指针。

主动传感器的分辨率决定「看得清多大的目标」。同一部传感器，分辨率设得越大，对大船越远，对小船越容易看不见。这个选择存在组件的 `Resolution` 里，也存在接触的 `Resolution` 里。

## 一个战斗子脉冲

表结构对应的顺序如下。这是数据流，不是源码里的函数名。

1. 决定这一拍的秒数。普通移动用 `SubPulseLength`。有 1 秒子脉冲时，近距离机动再切细。星云里舰队速度上限是 `MaxNebulaSpeed`。
2. 沿订单或当前航向推进舰队。写 `IncrementStart*` 和结束坐标。轨道任务改方位和轨道半径，不改成直线冲出星系。
3. 需要时重算天体位置。
4. 用主动、热、电磁和电子侦察更新接触。
5. 火控表决定这一拍打谁、点防御开不开、打多远、这一拍是否已经开过火（`FiredThisPhase`）。
6. 束射武器看充能、跟踪速度和距离。导弹从发射架进入 `FCT_MissileSalvo`。
7. 齐射按自己的速度和制导方式追目标，到引爆距离就结算。
8. 命中后先护盾 `CurrentShieldStrength`，再装甲，再内部组件。坏掉的部件进 `FCT_DamagedComponent`，修进 `FCT_DamageControlQueue`。
9. 点防御这一拍如果已经打过，`FiredThisPhase` 防止同一门近防再打一次。
10. 写 `FCT_GameLog` 和事件。事件类型在 `DIM_EventType`，玩家可以关掉某些事件。

舰上的延迟也是数据：`FireDelay`、`SensorDelay`、`LastFiringTime`、`LastMissileHitTime`、`LastBeamHitTime`。不是用一个全局冷却表。

## 导弹

设计好的型号在 `FCT_MissileType`。飞行中的弹在 `FCT_MissileSalvo`。齐射里每一发的剩余状态几乎一样，所以 `FCT_Missile` 只记这个齐射还剩几发、还剩几个诱饵。不要每发导弹一个完整对象。

型号上能看到一次设计的全部结果：体积、速度、续航秒数、最大航程、燃料、弹头伤害、主动传感器、热信号、电磁信号、电子对抗、二级弹、诱饵数量、重定向、激光弹头、潜伏。样本满足：

`MaxRange = Speed × Endurance`

飞镖反舰导弹速度 13822.2 km/s，续航 3423.2 秒，航程 47316155 km，乘积一致。燃料在设计时就折进续航，飞的时候齐射只减时间和距离，不再按发动机公式重算。

齐射自己带坐标、上一拍坐标、增量起点、目标类型和 ID、制导方式、当前引爆距离、末速度。二级分离和重定向改的是齐射行，不是回型号表。

## 损伤和生存

装甲在舰级上是厚度和宽度（`ArmourThickness`、`ArmourWidth`），不是一个血量整数。护盾有最大值和当前值。组件有 `HTK`（打掉它需要的命中次数）和爆炸概率。有的武器带 `IgnoreShields`、`IgnoreArmour`、`ArmourRetardation`、`WeaponToHitModifier`。

电子系统和船体系统分开。电子损伤有自己的损伤控制（组件上的 `ElectronicSystem`、`ElectronicCTD`，舰上有 `AutomatedDamageControl`）。商业船和军舰的损伤控制等级不同（`DCRating`、`ESMaxDACRoll`、`MaxDACRoll`）。

摧毁不是把船行删掉。`Destroyed` 还在，残骸、救生舱、幸存者、战俘分别进 `FCT_Wrecks`、`FCT_Lifepods`、`FCT_Survivors`、`FCT_Prisoners`。打捞和情报是后续订单，不是击毁瞬间的附带效果。

## 后勤和殖民地

`FCT_Population` 是一颗殖民地的账本，不是一个城市对象。里面有人口、效率、不满、燃料库存、维护补给、11 种矿物的现存量、储备和上次存量、地形改造状态、地面勘测、是否军管。矿物矿藏在 `FCT_MineralDeposit`：储量、当前开采难度、原始难度。难度随开采变差，所以矿不是一个固定产出。

舰上的油是 `FCT_Ship.Fuel`，维护补给是 `CurrentMaintSupplies`，弹药是 `FCT_ShipWeapon` 的数量。补给、加油、装弹是订单和优先级（`RefuelPriority`、`ResupplyPriority`），不是自动补满。

民用航运是普通舰队加上 `CivilianFunction` 和航运公司 ID。税收和贸易余额在 `FCT_PopTradeBalance`、`FCT_ShippingWealthData`。民用和军用共用移动和星系，不共用舰级限制：商业舰级有 `Commercial` 旗标，速度公式和军舰相同，但引擎功率倍数低。

质量驱动器的在途矿石是 `FCT_MassDriverPackets`，有自己的坐标，不是瞬间到账。

## 科研

科技是图，不是线性等级。节点有两个前置。种族已经会的技术在 `FCT_RaceTech`，可以标记过时。当前项目和暂停项目分开（`FCT_ResearchProject`、`FCT_PausedResearch`、`FCT_ResearchQueue`）。实验室数量和科学家能力决定进度，游戏级 `ResearchSpeed` 是全局乘数。

`DIM_TechType` 这版有两百多种，每一行是一个可研究的数值，例如电容充能、激光波长、火控距离、火控速度、导弹弹头、护盾再生、跃迁效率。设计器读这些已研究数值，生成组件行。战斗不读科技表。

## 地面战

地面单位也是树。`FCT_GroundUnitFormation` 有父编制、所在人口或舰船、战场位置、是否民用、是否拿来补充。模板和具体单位分开。能力、装甲、基础类型在 `DIM_` 里。

战斗不是每个子脉冲都打。`MinGroundCombatPeriod` 是 28800 秒。`FCT_RaceGroundCombat` 只记某个种族在某天体上连续打了多少回合。轨道轰炸是舰对地面编制的支援，不是另一套武器公式。舰上有 `AssignedFormationID`，编制上有 `ShipID`。

## 非玩家和事件

NPR、蜂群、入侵者、先驱者、遗迹种族都是 `FCT_Race` 上的不同数据，加上游戏级开关（`GenerateNPRs`、`StarSwarm`、`Invaders`、`Precursors`、`Eldar`）。他们用作战群 `DIM_OperationalGroup` 和设计哲学来决定造什么、研什么，然后仍然写入同一套舰队、舰级和订单。

事件是 `DIM_EventType` 加上日志。玩家可以按类型关事件、改颜色。事件是模拟的输出，不是驱动模拟的脚本。

## 做兵棋时建议照这个拆

- 权威状态放数据里，界面只发订单和读快照。
- 战斗时间细，经济时间粗，用「上次结算时刻」避免重复结算。
- 科技在设计时折进组件。运行时只读组件和舰级上的数。
- 接触和真实单位分开。估计舰级可以错。
- 导弹用齐射。数量和诱饵是齐射上的整数。
- 订单是数据。动作类型声明自己需要什么目标。玩家和 AI 共用解释器。
- 击毁之后留下残骸、救生舱和情报，不要立刻删掉单位。
- 位置用「这一拍的起点和终点」。插值留给显示。

## 不建议照搬的

- 一百五十张宽表和含糊的列名。新游戏用更窄的表或组件，但保留上面的边界。
- 在 UI 线程里扫全库做探测。路径和传感器屏会一次生成很多行，每行如果再做重计算就会卡住。
- 把显示字符串和规则焊在一起。命令名、列名、SQL 片段必须和给玩家看的字分开。
- 用一个全局异常号代替错误处理。这版用「功能号 + 异常文本」弹窗，调用方缺少「没有选中项」这种前置检查。

## 表应该怎么找

| 问题 | 先看 |
| --- | --- |
| 现在是哪一秒 | `FCT_Game`，`FCT_Increments` |
| 舰队在哪、下一跳去哪 | `FCT_Fleet`，`FCT_MoveOrders`，`DIM_MoveAction` |
| 星系怎么连 | `FCT_System`，`FCT_JumpPoint`，`FCT_SystemBody` |
| 船的纸面性能 | `FCT_ShipClass`，`FCT_ShipDesignComponents` |
| 船现在的油和盾 | `FCT_Ship` |
| 看见了什么 | `FCT_Contacts`，`FCT_AlienClass` |
| 这一拍打谁 | `FCT_FireControlAssignment`，`FCT_WeaponAssignment` |
| 飞行中的导弹 | `FCT_MissileSalvo`，`FCT_Missile`，`FCT_MissileType` |
| 殖民地账 | `FCT_Population`，`FCT_MineralDeposit` |
| 会哪些科技 | `FCT_TechSystem`，`FCT_RaceTech` |
| 地面编制 | `FCT_GroundUnitFormation`，`FCT_RaceGroundCombat` |
