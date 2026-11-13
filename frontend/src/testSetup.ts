// 单元测试默认按中文渲染：预装 zh 词表，否则组件断言会看到英文回落。
// 生产环境由 main.tsx 首屏 / I18nProvider 切换语言时按需加载同一份词表。
import './i18n/catalog.zh';
// 测试模块在顶层同步读取 fallbackCatalogRaw（目录断言覆盖率很高），因此在这里同步安装；
// 生产包不再静态 import 这份 1.65 MB 快照，改由 catalog.loadFallbackCatalog 动态加载。
import generatedCatalog from './generated/catalog.generated.json';
import { installFallbackCatalog, type GeneratedCatalogDocument } from './catalog';

installFallbackCatalog(generatedCatalog as unknown as GeneratedCatalogDocument);
