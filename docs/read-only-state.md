# 地图、连线与装备属性只读查询

本组五项工具不修改游戏状态，不安装观察钩子，也不需要开启变更权限。游戏内结构读取在框架线程完成，使用已安装 SDK 的完整结构复制和有界数组；不可读、布局不符或数据不全时返回未知。参数以连接后的完整 `tools/list` 目录为准。

| 工具 | 输入与用途 | 解释边界 |
| --- | --- | --- |
| `convert_map_coordinates` | 明确的 `mapId`、`sourceSpace=world/map/texture` 和 `position`，转换三个坐标空间。 | world 使用 x/z，可带高度 y；map 和 texture 使用 x/y，不推测高度或楼层。离线可读数据表时也可转换。 |
| `get_map_flags` | 读取当前原生地图旗标及所属地图、区域。 | XFloat/YFloat 是世界 X/Z；旗标高度未知。原生 MapMarkerBase 的 X/Y 单独保留，不能作为纹理像素转换。无旗标与代理不可读是两种状态。 |
| `find_nearest_aetheryte` | 区域内按世界平面距离排序；默认当前位置、主水晶、最多五项。可提供区域和世界坐标。 | 使用同区域 Level 坐标，缺失时仅在 MapMarker 纹理匹配唯一时回退。不是寻路、可达性或费用判断。默认不按解锁筛选；`unlockedOnly=true` 需要已有传送列表，城市碎片的独立解锁状态保持未知。不会刷新原生传送列表。 |
| `get_character_tethers` | 当前角色或已发现的完整 `gameObjectId` 字符串；`direction=both/outgoing/incoming`。 | 最多扫描 2048 对象槽位、返回 100 条连接。保留原生连接槽、完整 64 位 ID、原始进度字节和未解析目标。当前帧为空不证明历史上没有连线。 |
| `get_item_stat_breakdown` | 二选一：未镶嵌模板 `itemId/isHq`，或实际容器 `container/slot/expectedItemId`。 | 实际槽位只支持已加载的随身包、装备和兵装库；expectedItemId 包含 HQ 编码，防止槽位被替换。返回基础、HQ、魔晶石原值/有效值/溢出和上限输入。 |

坐标示例（数字仅展示格式，不是移动指令）：

```json
{"mapId":364,"sourceSpace":"world","position":{"x":-107.2,"y":16.02,"z":-40.17}}
```

装备模板和实际槽位示例：

```json
{"itemId":47184,"isHq":true}
```

```json
{"container":"EquippedItems","slot":2,"expectedItemId":1047184}
```

实际查询前重新读取装备或物品栏，复制当前容器、物理槽位和品质编码 ID。不得把界面排序当作物理槽位。模板不会代入角色拥有物品的魔晶石。

属性上限使用 `roundAwayFromZero(ItemLevel值 × 装备部位千分比 × 职能百分比 / 100000)`，HQ 特殊数值加在基础属性上。五个魔晶石槽位全部检查，包括中间空槽；缺失魔晶石行/等级时有效值保持未知，不用部分和冒充完整合计。缺失或与固有属性不一致的上限也不推测。固有数值不会被魔晶石上限截断。

这里计算的是**未同步的物品数据表与普通魔晶石属性**，不等于角色面板。等级/品级同步、古武自定义分配、特殊/套装效果、耐久影响、食物、药品和职业特性均未应用。`complete` 只说明本次受支持数据范围完整。保留 `unknown`、`materiaComplete`、`unknownSources`、`unknownLocations` 和 `truncated` 的含义。

实现对照了 ECommons 的能力分类，但未引入其运行时依赖。坐标转换使用当前 Dalamud SDK 的 [MapUtil](https://github.com/goatcorp/Dalamud/blob/master/Dalamud/Utility/MapUtil.cs) 与 Excel Map 参数，纹理约定见 [MapCoordinates](https://github.com/xivapi/ffxiv-datamining/blob/master/docs/MapCoordinates.md)。属性字段和舍入规则见 [ItemStats](https://github.com/xivapi/ffxiv-datamining/blob/master/docs/ItemStats.md)；原生旗标和连线使用本地 FFXIVClientStructs 布局。通过两个 SDK 的隔离测试不等于所有区服都完成游戏内验证。
