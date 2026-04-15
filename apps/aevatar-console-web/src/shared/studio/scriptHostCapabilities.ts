import type { StudioAppContext } from './models';

export type EmbeddedOnlyCapability = 'ask-ai' | 'draft-run';

export function formatStudioHostModeLabel(mode: StudioAppContext['mode']): string {
  return mode === 'embedded' ? '嵌入式 Host' : '代理 Host';
}

export function getStudioHostModeTooltip(mode: StudioAppContext['mode']): string {
  if (mode === 'embedded') {
    return '当前 Studio 会话运行在嵌入式 Host 中，可以直接测试运行，也可以使用 AI 辅助生成脚本修改。';
  }

  return '当前 Studio 会话运行在代理 Host 中。这里可以继续校验、保存和发布，但测试运行与 AI 辅助需要切换到嵌入式 Host。';
}

export function getEmbeddedOnlyUnavailableMessage(
  capability: EmbeddedOnlyCapability,
): string {
  if (capability === 'draft-run') {
    return '测试运行需要嵌入式 Host。请把当前 Studio 会话从代理模式切换到嵌入式模式后，再运行这个草稿。';
  }

  return 'AI 辅助需要嵌入式 Host。请把当前 Studio 会话从代理模式切换到嵌入式模式后，再生成脚本修改。';
}
