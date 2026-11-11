# StockSim — A股模拟做T系统

基于 `docs/` 中的需求、技术实现、落地与数据库文档生成的可运行项目。

> **模拟交易，非实盘投资建议。**

文档：`docs/需求文档.md`、`docs/技术实现文档.md`、`docs/落地实施文档.md`、`docs/数据库文档.md`（表结构与写库操作；**库变更须同步**）。

## 技术栈

| 层级 | 实现 |
|------|------|
| 运行时 | .NET 8 |
| Web | ASP.NET Core MVC（开放访问，无需登录；ABP 风格分层） |
| 数据 | EF Core 8 + **SQLite（默认）** / **SQL Server 2012+** |
| Worker | `BackgroundService`：策略评估 + 日终结算 |
| 做T行情 | `ReplayMarketDataProvider`（`data/replay` CSV） |
| 个股详情 | `EastMoneyStockDetailProvider`（东方财富公开接口，失败回退模拟） |
| 图表 | **ECharts 5.5.1**（jsDelivr CDN） |
| UI | Bootstrap 5 + `stock-terminal.css` |
| AI | `StockSim.AI.Cursor` 文件交换（半自动确认） |
| 测试 | xUnit |

## 解决方案结构

```
StockSim.slnx
├── docs/                          需求 / 技术 / 落地 / 数据库文档
├── src/
│   ├── StockSim.Domain            实体、费用、风控、撮合、策略评估、开市判断
│   ├── StockSim.Application       应用服务、DTO
│   ├── StockSim.Infrastructure    EF Core
│   ├── StockSim.Web               MVC 监控面板 + 个股终端
│   └── StockSim.Worker            自动做T / 日终
├── adapters/
│   ├── StockSim.MarketData.Replay # CSV 回放 + 东财详情 + 模拟回退
│   └── StockSim.AI.Cursor
├── data/replay                    回放样例
├── data/ai                        AI 请求/响应交换目录
├── scripts/                       一键部署 + SQL 脚本
└── test/StockSim.Tests.Domain
```

## 一键部署（推荐）

双击 `scripts\一键部署.bat`，或在仓库根目录执行：

```powershell
.\scripts\deploy-local.ps1 -Start
```

脚本会：检查 .NET SDK → 发布到 `publish\web` / `publish\worker` → 写入共用 SQLite 绝对路径 → 启动 Web + Worker → 打开浏览器。

发布时若已有部署库，询问 **保留(K) / 重建(R)**；若存在测试库 `src\StockSim.Web\stocksim.db`，再询问是否 **用测试库覆盖部署库 [Y/N]**（默认 N）。也可：

```powershell
.\scripts\deploy-local.ps1 -KeepDatabase -SeedFromTestDatabase -Start
.\scripts\deploy-local.ps1 -SkipSeedFromTestDatabase -Start
```

| 脚本 | 作用 |
|------|------|
| `scripts\一键部署.bat` | 发布并启动（双击即可） |
| `scripts\deploy-local.ps1` | 仅发布；加 `-Start` 同时启动 |
| `scripts\start-local.ps1` | 启动已有 `publish` |
| `scripts\stop-local.ps1` | 停止 Web / Worker |

默认访问：`http://localhost:5167`（无需注册/登录）。

## 开发模式快速开始（SQLite）

**Visual Studio（推荐）**：打开 `StockSim.slnx`，工具栏启动配置选 **「Web + Worker」** 后 F5（由 `StockSim.slnLaunch` 定义，会同时调试 Web 与 Worker）。若下拉没有该配置，先到 **工具 → 选项 → 环境 → 预览功能** 勾选 **Enable Multi-Project Launch Profiles**，再右键解决方案 → **Configure Startup Projects…**。

命令行：

```powershell
cd d:\Projects\StocksMarketWeb\StockMarketSimulationSoftware\StockMarketSimulationSoftware-main
dotnet restore
dotnet test
dotnet run --project src\StockSim.Web
# 另开终端：
dotnet run --project src\StockSim.Worker
```

浏览器打开提示地址即可使用（无需登录）。种子数据含：

- 模拟账户（100 万现金 + 茅台/平安底仓）
- 默认做T池与策略

> 请先停止旧 Web/Worker 进程再编译/启动，避免 DLL 被锁定。

## 切换 SQL Server 2012+

1. 执行 `scripts/init-sqlserver.sql` 建库  
2. 参考 `src/StockSim.Web/appsettings.SqlServer.json`，设置 `Database:Provider=SqlServer` 与连接字符串  
3. Worker 同步修改连接字符串  

## 主要功能入口

| 页面 | 说明 |
|------|------|
| 总览 | 资产、今日盈亏 |
| 股票池 | **一股一策**：入池 AI 生成；每晚 22:00 自动更新；报价·估值；运行状态 |
| 个股详情 | 同花顺风格：分时/日K/资金流/估值；**点击日K下钻分时K** |
| 策略 | 参数版本、绑定池 |
| 成交/持仓 | 手工模拟下单与明细 |
| 盈亏 | 日终结算与历史 |
| AI工作台 | 选股/策略建议 → 查阅分析 → 确认；策略可**新增** |

## Cursor Agent 选股（真实推理）

Cursor **不支持**用账号密码调 Agent；请用 API Key 或浏览器登录当前账号：

```powershell
# 推荐：双击 scripts\login-cursor-agent.bat
# 或：
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-cursor-agent.ps1 -Login
```

或到 [Dashboard → Integrations](https://cursor.com/dashboard/integrations) 创建 API Key，写入用户环境变量 `CURSOR_API_KEY`，或本地文件 `secrets\cursor-api-key.txt`（已 gitignore），然后**重启 Web**。

默认 `AI:Cursor:Enabled=true` 且 **不回退占位**：未登录时任务会 Failed 并显示原因。AI 工作台顶部有接通状态指示。

### 文件闭环（可选 / 回退）

1. 任务仍会在 `data/ai/` 留下 request/response 便于审计  
2. 仅占位模式：设 `AI:Cursor:Enabled=false`  
3. 在「查阅」页确认后才会改池；策略建议默认可新增  

## 第三方组件速查

| 类型 | 名称 |
|------|------|
| NuGet | EF Core Sqlite/SqlServer、Extensions.Http、xUnit、@cursor/sdk（桥接） |
| CDN | ECharts 5.5.1 |
| 数据源 | 东方财富公开行情 API |
| 规划未用 | 完整 ABP、Hangfire、SignalR、AdminLTE |

细节见 `docs/技术实现文档.md` 第 12 节。

## 测试

```powershell
dotnet test test\StockSim.Tests.Domain
```

覆盖费用、涨跌停拒单、撮合资金变化、策略触发。

## 与文档决策对齐

| 决策 | 当前默认 |
|------|----------|
| 运行时 | .NET 8 分层（开放 Web / ABP 风格） |
| 做T模式 | `SimIntradaySellAllowed` |
| 行情 | Worker CSV；详情东财（可回退模拟） |
| AI | 文件交换 + 人工确认；策略可新增 |
| 图表 | ECharts |
| 部署 | Web + Worker 双进程 |
