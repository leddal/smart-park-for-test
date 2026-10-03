# 智慧公园 · 本地桌面演示

.NET 10 模块化单体、Vue 3 / TypeScript、PostgreSQL 17、Redis 7.4。面向单公园的桌面后台、独立大屏与 PC 游客页。业务数据真实持久化；遥测、控制和平台协同明确为本地演示，不接入真实硬件、视频流或外部平台。

> 仅用于 localhost 演示。默认账号、数据库密码和 HTTP Cookie 配置不得直接用于公网。没有移动端、定位打卡、支付、实名或实际随申码认证。

## 启动

需要已运行的 Docker Desktop，使用 **Linux containers**。无需宿主机安装 .NET / Node / PostgreSQL / Redis。首次构建会下载镜像与依赖；构建完成后的地图、影像、音频与生产说明均来自本地。

在本项目根目录执行：

```cmd
if not exist .env copy .env.example .env
docker compose build api
docker compose build web
docker compose up --no-build -d
docker compose ps -a
docker compose logs migrate api web
```

逐个构建规避当前 Windows Docker Compose/Bake 在中文路径上并行构建时的 `x-docker-expose-session-sharedkey` 非 ASCII 请求头错误，不需要修改 Docker 全局配置。

若 `.env` 已存在，不要覆盖。无需复制也可使用 Compose 中的本地默认值。`migrate` 成功退出（Exit 0）是正常状态，常驻服务为 postgres、redis、api、web。更换端口修改 `.env` 的 `WEB_PORT`，例如 8088，再执行 `docker compose up -d`。

| 入口 | 地址 | 访问者 |
|---|---|---|
| 桌面后台 | http://localhost:8080/admin | 管理员、调度员 |
| 桌面我的任务 | http://localhost:8080/admin/operations/my-tasks | 作业人员 |
| 独立运行大屏 | http://localhost:8080/screen | 管理员、调度员 |
| PC 游客页 | http://localhost:8080/visitor | 匿名浏览、游客预约 |
| 就绪检查 | http://localhost:8080/api/health/ready | 状态检查 |

仅 web 绑定 `127.0.0.1:8080`；数据库和 Redis 不映射宿主机端口，不影响已有 5432 / 6379 实例。Compose 项目名为 `smartpark-demo`，不会接管已有同名业务数据库或用户容器。

## 演示账号

默认密码均为 `.env.example` 中的 `ParkDemo!2026`，由 `DEMO_PASSWORD` 控制首次创建：

| 用户名 | 角色 | 权限边界 |
|---|---|---|
| admin | 管理员 | 业务与系统维护、演示开关、内部账号 |
| dispatcher | 调度员 | 事件派单、验收、本地控制指令、手工遥测/客流、协同重试、活动核销；周期模拟启停与异常/恢复场景仅管理员 |
| worker | 作业人员 | 仅分配给自己的桌面工单与照片 |
| worker2 | 第二作业人员 | 归属权限验证用；只能处理自己的工单 |
| visitor | 游客 | 公开服务、自己的预约 |

游客也可自行注册；注册不能选择内部角色。Cookie 为 HttpOnly；状态修改需要防伪令牌。登录失败启用锁定与限流。修改环境变量不会重置数据库中已有账号的密码，内部账号须由管理员显式管理；无邮件找回流程。

## 首次演示

种子只执行一次，含区域、各类演示设备、设施、植物、活动、公告、阈值、历史指标和样例任务。动态模拟默认关闭，重启后也关闭。历史数据会过期，页面应显示来源与时间；不将种子数据称为实际设备在线。

### 跨模块处置闭环

1. 管理员在智能物联选土壤设备，执行“模拟超阈值”。也可手工录入合法单位的异常值。进入告警中心，查看关联事件。
2. 调度员在事件详情派发工单，选择作业人员、类型、资产、期限和对应检查项。
3. worker 登录“我的任务”，接单、开始，填写文字与检查结果；可选择本地 PNG/JPEG/WebP 照片（每张最多 5 MiB，每次最多 5 张），提交验收。
4. 调度员可以带原因驳回，作业人员再次反馈；之后验收通过。资产维护历史随验收产生。
5. 管理员执行“模拟恢复”。**告警恢复不会自动办结事件或工单。**
6. 调度员填写办结说明并手工办结事件。活跃告警或未完成工单会阻止普通办结。
7. 在外部协同查看事务生成的同步记录、失败重试及本地回执。成功文字表示“本地模拟成功，未发送到外部平台”，不是官方平台接通。

同轮异常不会反复建事件；恢复后再次异常会新建一轮。旧采集时间只入历史；重复幂等标识不重复处理。误报需原因并抑制至恢复，而不是删除历史。

### 其它验收入口

- 数据平台：下载 `samples/assets.csv` 或 GeoJSON，预览、查看行级错误后确认；DOM 使用本地 PNG/JPEG 加已配准的西南/东北坐标。坐标为 WGS84 `[经度,纬度]`。
- 资产信息：新增、编辑、退役；查看分类、植物碳储量估算及关联事件/任务；物码二维码只指向公开介绍。
- 智慧服务：公告人工发布；活动与独立场次、名额管理；游客预约/开始前取消，内部人员核销。免费即时确认，每游客每场一条有效预约。
- 智慧导览：在地图选择资产起终点；只沿已导入且共享节点的园路计算。无法连通显示无可用路线，不画直线冒充导航。
- 智能控制：灌溉启停与时长、路灯亮度、广播文本以及失败/超时示例均为本地模拟。广播音频需用户显式点击预听，不代表硬件播放。
- 总览与大屏：10 秒轮询、服务器聚合趋势、图层开关、来源/过期状态；Redis 不可用时退回数据库。
- 视频与五平台：查看示例元数据、具体生产要求和模拟记录，没有实际视频画面、外网同步或接入凭据配置。

## 隔离验证

验证配置有三个独立数据库：`smartpark_test`（集成）、`smartpark_e2e`（浏览器）、`smartpark_benchmark`（基准），使用 `smartpark-verify` 项目独立卷。不能将验证连接指向演示库或用户数据库。基准程序另有数据库名及显式许可校验。

```cmd
for %s in (api web backend-tests frontend-tests e2e benchmarks) do docker compose -f compose.verify.yaml -p smartpark-verify build %s
docker compose -f compose.verify.yaml -p smartpark-verify run --rm backend-tests
docker compose -f compose.verify.yaml -p smartpark-verify run --rm frontend-tests
docker compose -f compose.verify.yaml -p smartpark-verify up --no-build -d web
docker compose -f compose.verify.yaml -p smartpark-verify run --rm --no-deps e2e
docker compose -f compose.verify.yaml -p smartpark-verify run --rm benchmarks
```

前端实际 scripts：`typecheck`、`lint`、`test:unit`、`build`、`test:e2e`。浏览器验证仅桌面视口。后端采用真实 PostgreSQL 与 Redis，不以 EF InMemory 模拟事务。基准会在隔离库生成约 10 万遥测和 1 万资产，消耗 CPU、内存与磁盘；建议避免与其它重负载同时运行，不修改正式索引或清宿主机缓存。

结果目录 `artifacts/`（不纳入版本库）：后端 TRX、Playwright 报告/截图、`benchmarks/<UTC时间>/results.json`、查询计划及实测章节。**命令入口存在不等于已经通过**；实际执行记录与未完成项见 [优化验证报告](docs/优化验证报告.md)。

## 停止、重启与数据

```cmd
docker compose stop
docker compose start
docker compose restart api web
```

数据库、上传文件和 Data Protection 密钥使用命名卷。容器重建不清数据；正常启动执行迁移而不是重建数据库。模拟重启默认停用，模拟控制状态不会冒充现场持续运行。改变环境变量和重新构建不会刷新种子历史或重置密码。

不自动清理遥测、事件、任务、审计或已绑定照片；长期开模拟会增长数据库，正式留存/归档期限需要另行确认。Redis 是可重建缓存，不保存唯一业务事实。

### 备份与恢复

先停业务写入，使用默认数据库名/用户的示例：

```cmd
docker compose stop web api
docker compose exec -T postgres pg_dump -U park -d smartpark_demo > park-backup.sql
```

同时备份 `smartpark-demo_uploads` 和 `smartpark-demo_keys`；只有 SQL 没有上传文件不能恢复照片和影像。恢复应先在独立新库验证，使用对应版本 PostgreSQL 的 `psql`，不要覆盖运行库。环境变量已改名时需调整上述参数。

> 破坏性警告：`docker compose down -v` 会永久删除该项目的数据库、上传和密钥卷。不要当作普通停止命令。本实施过程不执行删除卷、清库或改动用户现有实例的操作。

## 设计与范围

- [模块设计与关联说明](docs/模块设计与关联说明.md)：逐子服务映射、模块职责、权限、Mermaid 关联/实体/时序图。
- [生产要求与降级说明](docs/生产要求与降级说明.md)：界面主题、当前能力、正常生产所需数据与服务。
- [优化验证报告](docs/优化验证报告.md)：技术因果、可复现实测、计算方法、执行状态与局限。

本版不是生产部署或高可用平台：没有真实视频/协议接入、正式测绘、权威碳核算、移动专项、多租户、支付和实名认证，也没有后续硬件接入目标。界面中的生产要求是能力边界说明，不代表额外承诺。
