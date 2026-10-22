import { Modal, Tag, Typography } from 'antd';
import { APP_VERSION } from '../version';

type Props = { open: boolean; onClose: () => void };

const capabilities: { name: string; color: string; description: string }[] = [
  { name: '工作流引擎', color: 'green', description: '节点图编译为可执行中间表示，并按流水线段调度执行' },
  { name: '递归中间表示', color: 'purple', description: '条件分支、并行区域与嵌套结构化流程' },
  { name: '强类型视觉数据', color: 'blue', description: '端口按 Image / 几何 / 数值 / 控制流做类型校验' },
  { name: '图像查看器', color: 'cyan', description: '缩放平移、ROI 编辑与结果覆盖层叠加' },
  { name: '采集流水线', color: 'geekblue', description: '相机连续采集、软硬触发与同步帧组' },
  { name: '作业版本', color: 'gold', description: '产品与配方参数按版本冻结、发布与追溯' },
  { name: '追溯', color: 'magenta', description: '运行记录、判定结果与预览归档查询' },
  { name: '视觉工具 SDK', color: 'orange', description: '插件化视觉工具加载与包签名校验' },
  { name: '强类型几何', color: 'volcano', description: '点、线、圆、旋转矩形等几何量测与运算' },
  { name: '标定 / 坐标系', color: 'lime', description: '平面标定、坐标变换链解析与坐标系管理' },
  { name: '二维机器人引导', color: 'red', description: '眼在手 / 眼在手外引导与 TCP 补偿' },
  { name: '设备标签', color: 'processing', description: '与 PLC 等设备读写标签、等待条件' },
  { name: '实时诊断', color: 'cyan', description: '资产健康、实时事件流与故障签名趋势分析' }
];

export default function AboutPanel({ open, onClose }: Props) {
  return (
    <Modal open={open} onCancel={onClose} footer={null} width={820} title="关于 VisionStudio">
      <div className="about-intro">
        <div className="about-title">VisionStudio Workflow Core <Tag color="blue">V{APP_VERSION}</Tag></div>
        <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }}>
          面向工业视觉的工作流设计与运行平台：在画布上编排节点并编译为可执行的递归中间表示，覆盖图像采集、几何量测、标定、机器人引导、设备交互与生产追溯。
        </Typography.Paragraph>
      </div>
      <div className="about-capabilities">
        {capabilities.map((item) => (
          <div className="about-capability" key={item.name}>
            <Tag color={item.color}>{item.name}</Tag>
            <span>{item.description}</span>
          </div>
        ))}
      </div>
    </Modal>
  );
}