import { nodeCategory } from '../toolboxModel';
const paths = {
  acquisition: 'M3 7h4l2-3h6l2 3h4v13H3Z M16 13a4 4 0 1 1-8 0 4 4 0 0 1 8 0',
  preprocess: 'M4 6h16M4 12h16M4 18h16M8 3v6M16 9v6M10 15v6',
  feature: 'M8 3H3v5M16 3h5v5M3 16v5h5M21 16v5h-5M16 12a4 4 0 1 1-8 0 4 4 0 0 1 8 0',
  measurement: 'M3 7h18v10H3ZM7 7v5M11 7v3M15 7v5M19 7v3',
  geometry: 'M4 19 12 4l8 15ZM4 19h16M12 4v15',
  calibration: 'M3 3h18v18H3ZM3 9h18M3 15h18M9 3v18M15 3v18',
  coordinate: 'M6 3v15h15M6 18l9-9M6 3l-3 3M6 3l3 3M21 18l-3-3M21 18l-3 3',
  flow: 'M12 3v5M12 8l-7 5v7M12 8l7 5v7M2 20h6M16 20h6',
  device: 'M5 4h14v16H5ZM9 8h6M9 12h6M9 16h2M2 8h3M19 8h3M2 16h3M19 16h3',
  robot: 'M5 21h14M8 21v-5l6-5-5-5M5 6a2 2 0 1 0 4 0 2 2 0 0 0-4 0M14 11l4-6 3 2-2 4M14 11l3 3',
  plugin: 'M9 3H3v6h3a3 3 0 0 1 0 6H3v6h6v-3a3 3 0 0 1 6 0v3h6v-6h-3a3 3 0 0 1 0-6h3V3h-6'
};
export default function NodeCategoryIcon({ type }: { type: string }) {
  return <svg className="node-category-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={paths[nodeCategory(type)]} /></svg>;
}
