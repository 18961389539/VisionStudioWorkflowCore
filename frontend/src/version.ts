import { version } from '../package.json';

/** 产品版本号：单一来源为 frontend/package.json，展示时统一按 `V{APP_VERSION}` 格式渲染。 */
export const APP_VERSION = version;