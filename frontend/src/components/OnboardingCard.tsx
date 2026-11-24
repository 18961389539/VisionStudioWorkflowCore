/**
 * 首次上手引导（2026-11-23）。
 *
 * 工具箱有 242 个节点、模板库是唯一入口，但新用户打开编辑器只看到一张空画布和几条菜单——
 * 从"选哪个模板"到"怎么发布到产线"之间没有任何路径提示。这张卡片给一条五步主线，
 * 每步都能直接打开对应面板；关掉后写入本地偏好不再自动出现，可在「工具」菜单重新打开。
 *
 * 刻意不用 antd Card：卡片要在深浅两套主题下都贴合画布（背景/边框走主题令牌），
 * 而这块样式只有一处消费方，内联样式即可，不必占用主包 CSS 预算。
 */
import { Button } from 'antd';

type Props = {
  onClose: () => void;
  onOpenTemplates: () => void;
  onOpenCameras: () => void;
  onOpenCommand: () => void;
  onOpenValidation: () => void;
  onOpenJobs: () => void;
};

type Step = { title: string; hint: string; action: string; run: () => void };

export default function OnboardingCard({ onClose, onOpenTemplates, onOpenCameras, onOpenCommand, onOpenValidation, onOpenJobs }: Props) {
  const steps: Step[] = [
    { title: '选一个起点', hint: '模板库里挑能直接跑的流程，比空画布快', action: '模板库', run: onOpenTemplates },
    { title: '接上设备', hint: '相机、PLC、机器人各自注册后才能在流程里引用', action: '设备', run: onOpenCameras },
    { title: '调节点参数', hint: '双击节点打开配置；Ctrl+K 可以搜任何命令', action: '命令面板', run: onOpenCommand },
    { title: '验证流程', hint: '先点顶栏「校验」，再用数据集跑一遍判定', action: '数据集验证', run: onOpenValidation },
    { title: '发布到产线', hint: '配方管理里发布版本，操作员端才能启动', action: '配方管理', run: onOpenJobs },
  ];

  return <aside aria-label="上手引导" style={{
    // 右上角：左上会被居中的命令面板压住一半，左下/右下分别被缩放控件和小地图占用
    position: 'absolute', top: 12, right: 12, zIndex: 45, width: 310, padding: '12px 14px',
    border: '1px solid var(--vs-border-frame)', borderRadius: 10, background: 'var(--vs-bg-card)', color: 'var(--vs-text-2)',
  }}>
    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 8, marginBottom: 8 }}>
      <strong style={{ fontSize: 13, color: 'var(--vs-text-1)' }}>从零到一条产线流程</strong>
      <Button type="text" size="small" aria-label="关闭上手引导" onClick={onClose}
        style={{ flex: 'none', width: 22, height: 22, padding: 0, color: 'var(--vs-text-5)' }}>×</Button>
    </div>
    <ol style={{ display: 'grid', gap: 8, margin: 0, padding: 0, listStyle: 'none' }}>
      {steps.map((step, index) => <li key={step.title} style={{ display: 'grid', gridTemplateColumns: '18px minmax(0, 1fr)', gap: 6 }}>
        <span style={{
          display: 'grid', placeItems: 'center', width: 18, height: 18, borderRadius: 9,
          background: 'var(--vs-bg-track)', color: 'var(--vs-text-4)', fontSize: 11, fontVariantNumeric: 'tabular-nums',
        }}>{index + 1}</span>
        <div style={{ display: 'grid', gap: 1 }}>
          <span style={{ display: 'flex', alignItems: 'baseline', gap: 6, flexWrap: 'wrap' }}>
            <span style={{ fontSize: 12, color: 'var(--vs-text-2)' }}>{step.title}</span>
            <Button type="link" size="small" onClick={step.run} style={{ padding: 0, height: 'auto', fontSize: 12 }}>{step.action}</Button>
          </span>
          <span style={{ fontSize: 11, lineHeight: 1.5, color: 'var(--vs-text-5)' }}>{step.hint}</span>
        </div>
      </li>)}
    </ol>
    <div style={{ marginTop: 8, fontSize: 11, color: 'var(--vs-text-5)' }}>
      关闭后不再自动出现，可在「工具」菜单重新打开。
    </div>
  </aside>;
}
