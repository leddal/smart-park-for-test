export type ProductionTopic = 'gis' | 'telemetry' | 'video' | 'control' | 'platforms' | 'eco' | 'carbon' | 'desktop' | 'deployment'
export interface ProductionHintContent { title: string; current: string; reason: string; production: string; supported: string }

export const productionHints: Record<ProductionTopic, ProductionHintContent> = {
  gis: { title: 'GIS 与底图', current: '本版在浏览器中渲染本地 GeoJSON 与已配准 DOM 图片，不加载在线瓦片。', reason: '演示数据轻量，未接入测绘与空间服务。', production: '需使用授权测绘数据、统一坐标系、GDAL 转换、PostGIS 空间索引及受控瓦片/影像服务。', supported: '支持轻量标准数据导入与图层展示；不支持自动配准或生产测绘。' },
  telemetry: { title: '物联遥测', current: '仅显示种子、人工录入和管理员显式开启的模拟数据。', reason: '本版没有真实设备、网关或公共遥测入口。', production: '需建设 MQTT/HTTP 网关、设备证书、签名、时钟同步、幂等与断网补传及单位治理。', supported: '支持台账、阈值、告警与内部模拟；不表示设备在线。' },
  video: { title: '视频资源', current: '仅维护摄像头资源台账，不播放视频流。', reason: '未部署视频网关、转码、授权与录像存储。', production: '需 GB28181/RTSP 平台、媒体网关、WebRTC/HLS 分发、鉴权、审计和录像存储。', supported: '支持资源说明与位置展示；不支持实际视频。' },
  control: { title: '设备控制', current: '灌溉、广播和路灯操作只生成本地模拟回执。', reason: '本版不接入现场协议或硬件安全联锁。', production: '需设备协议、应答确认、现场互锁、安全策略、授权分级与完整审计。', supported: '支持模拟状态与指令日志；不代表阀门、广播或灯具已动作。' },
  platforms: { title: '外部平台协同', current: '业务消息先发布到本机 RabbitMQ 队列，再由本地模拟消费者生成回执，不发起外网请求。', reason: '未取得官方接口规范、白名单和授权，也没有真实平台消费方。', production: '需官方字段规范、网络白名单、授权签名、回调、幂等、死信重投与错误码处理。', supported: '支持本地队列发布、退避重试、死信与分阶段日志演示；不代表已对接官方平台。' },
  eco: { title: '生态指数', current: '按 PM2.5、噪声、溶解氧演示公式计算环境综合分。', reason: '不是法规标准或完整生态评价。', production: '需采用主管部门认可的指标体系、采样质控、基准与评价方法。', supported: '显示公式、来源与数据不足状态。' },
  carbon: { title: '植物碳储量', current: '按 B=0.1×D²×H，C=B×0.5×44/12 估算。', reason: '未使用树种异速生长方程或核证方法。', production: '需树种参数、年龄与根系模型、持续调查和合规碳核算。', supported: '仅为非权威储量估算，不可用于交易或核证。' },
  desktop: { title: '桌面作业', current: '作业人员使用同一桌面后台提交文本和可选照片。', reason: '本版明确不建设移动端、定位、相机采集或打卡。', production: '移动作业通常需要专门客户端、HTTPS、位置/相机权限、弱网和隐私策略；移动端取消或提交必须沿用服务端状态机。', supported: '支持桌面任务流转与文件选择上传。' },
  deployment: { title: '生产部署与文件安全', current: '本地演示使用容器内本地文件和示例账号，不可直接公网部署。', reason: '未配置生产 TLS、独立凭据、对象存储或媒体安全处理链路。', production: '部署时必须使用 TLS、独立密钥与轮换凭据、受控对象存储、恶意文件扫描、EXIF 隐私处理、数据库与文件备份恢复演练，并限制管理端网络访问。', supported: '仅提供本地演示文件与受权限保护的读取接口；不承诺生产级存储或媒体扫描。' },
}
