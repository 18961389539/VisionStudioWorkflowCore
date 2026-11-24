import { describe, expect, it } from 'vitest';
import { emptyStationDraft, stationConfigJson, stationEnvText, validateStationDraft, type StationConfigDraft } from '../stationConfigDraft';

const valid: StationConfigDraft = {
  id: 'inspection-a',
  name: '正面检测',
  baseUrl: 'https://inspection-a.example/',
  token: 'vst_read_token',
  deliveryToken: 'vst_deliver_token',
  controlToken: 'vst_control_token',
  expectedJobId: 'front-cover',
  expectedVersion: '4',
  expectedWorkflowHash: '',
};

const fields = (draft: StationConfigDraft, context?: { existingIds?: string[]; stationCount?: number }) =>
  validateStationDraft(draft, context).map((issue) => issue.field);

describe('多工位配置草稿校验（对齐后端 IsValid）', () => {
  it('完整草稿没有告警', () => {
    expect(validateStationDraft(valid)).toEqual([]);
  });

  it('最少字段也可通过：只有 ID、名称、地址与读取令牌', () => {
    expect(validateStationDraft({ ...emptyStationDraft, id: 'a', name: 'A', baseUrl: 'http://localhost:5000', token: 'vst_x' }))
      .toEqual([]);
  });

  it('工位 ID 非法或重复', () => {
    expect(fields({ ...valid, id: 'inspection a' })).toContain('id');
    expect(fields({ ...valid, id: 'x'.repeat(65) })).toContain('id');
    expect(fields(valid, { existingIds: ['INSPECTION-A'] })).toContain('id');
  });

  it('跨主机 http 被拒绝，回环 http 允许', () => {
    expect(fields({ ...valid, baseUrl: 'http://inspection-a.example/' })).toContain('baseUrl');
    expect(fields({ ...valid, baseUrl: 'http://127.0.0.1:5180' })).not.toContain('baseUrl');
    expect(fields({ ...valid, baseUrl: 'http://[::1]:5180' })).not.toContain('baseUrl');
  });

  it('地址不得带账号、查询串或片段', () => {
    expect(fields({ ...valid, baseUrl: 'https://user:pass@inspection-a.example/' })).toContain('baseUrl');
    expect(fields({ ...valid, baseUrl: 'https://inspection-a.example/?a=1' })).toContain('baseUrl');
    expect(fields({ ...valid, baseUrl: 'https://inspection-a.example/#x' })).toContain('baseUrl');
  });

  it('令牌必须带 vst_ 前缀，且三个令牌不能重复', () => {
    expect(fields({ ...valid, token: 'read-token' })).toContain('token');
    expect(fields({ ...valid, deliveryToken: valid.token })).toContain('deliveryToken');
    expect(fields({ ...valid, controlToken: valid.deliveryToken })).toContain('controlToken');
  });

  it('期望版本与配方 ID 必须成对出现', () => {
    expect(fields({ ...valid, expectedVersion: '' })).toContain('expectedVersion');
    expect(fields({ ...valid, expectedJobId: '', expectedVersion: '4' })).toContain('expectedJobId');
    expect(fields({ ...valid, expectedWorkflowHash: 'abc' })).toContain('expectedWorkflowHash');
    expect(fields({ ...valid, expectedWorkflowHash: 'a'.repeat(64) })).not.toContain('expectedWorkflowHash');
  });

  it('工位数量到 32 个后拒绝继续添加', () => {
    expect(fields(valid, { stationCount: 32 })).toContain('id');
    expect(fields(valid, { stationCount: 31 })).not.toContain('id');
  });
});

describe('生成可粘贴的配置片段', () => {
  it('第一个工位生成完整骨架，且不含任何令牌', () => {
    const json = stationConfigJson(valid, 0);
    expect(JSON.parse(json)).toEqual({
      ProductionFleet: {
        Stations: [{
          Id: 'inspection-a', Name: '正面检测', BaseUrl: 'https://inspection-a.example/',
          ExpectedJobId: 'front-cover', ExpectedVersion: 4,
        }],
      },
    });
    expect(json).not.toContain('vst_');
  });

  it('追加工位只生成一个可粘进数组的对象', () => {
    expect(JSON.parse(stationConfigJson(valid, 2))).toEqual({
      Id: 'inspection-a', Name: '正面检测', BaseUrl: 'https://inspection-a.example/',
      ExpectedJobId: 'front-cover', ExpectedVersion: 4,
    });
  });

  it('未填期望目标时不写出这些键（后端要求 null 而非空串）', () => {
    const json = stationConfigJson({ ...valid, expectedJobId: '', expectedVersion: '', expectedWorkflowHash: '' }, 0);
    expect(JSON.parse(json).ProductionFleet.Stations[0]).not.toHaveProperty('ExpectedJobId');
    expect(JSON.parse(json).ProductionFleet.Stations[0]).not.toHaveProperty('ExpectedVersion');
  });

  it('令牌按列表下标写入环境变量，可选令牌留空时不生成对应行', () => {
    expect(stationEnvText(valid, 0)).toBe('ProductionFleet__Stations__0__Token=vst_read_token\nProductionFleet__Stations__0__DeliveryToken=vst_deliver_token\nProductionFleet__Stations__0__ControlToken=vst_control_token');
    expect(stationEnvText({ ...valid, deliveryToken: '', controlToken: '' }, 3)).toBe('ProductionFleet__Stations__3__Token=vst_read_token');
  });
});
