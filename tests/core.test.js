'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { parse, validate, calculate } = require('../core.js');
const milk = { name: '牛奶', price: 59.9, amount: 250, unit: 'ml', quantity: 24 };

test('中文规格和到手价转换成每升价格', () => {
  assert.deepEqual(parse('牛奶 250ml×24盒，券后59.9元'), milk);
  const result = calculate(milk);
  assert.equal(result.total, 6);
  assert.equal(result.dimension, 'volume');
  assert.ok(Math.abs(result.unitPrice - 59.9 / 6) < 1e-12);
});

test('不同包装比较：便宜的一箱不一定单位价更低', () => {
  const other = parse('牛奶B 200ml×24盒，到手49.9元');
  assert.ok(other.price < milk.price);
  assert.ok(calculate(other).unitPrice > calculate(milk).unitPrice);
});

test('全角、大小写、中文单位和多层包装', () => {
  assert.equal(parse('MAX牛奶250ml×24盒，59.9元').name, 'MAX牛奶');
  assert.equal(parse('牛奶 ２５０ＭＬ＊６盒×４提，券后５９．９元').quantity, 24);
  assert.equal(calculate(parse('水 1.5L×6瓶，18元')).total, 9);
  assert.equal(calculate(parse('米 500克×4袋，20元')).unitPrice, 10);
  assert.equal(calculate(parse('米 2公斤，￥20')).unitPrice, 10);
});

test('明确的到手价优先于原价，但多个到手价拒绝猜测', () => {
  assert.equal(parse('牛奶250ml×24盒，原价99元，券后59.9元').price, 59.9);
  assert.throws(() => parse('牛奶250ml×24盒，券后59元，到手60元'));
  assert.throws(() => parse('牛奶250ml×24盒，59元或60元'));
});

test('件数比较支持电池和赠品的人工核对结果', () => {
  const a = calculate({ name: '电池A', price: 19.9, amount: 1, unit: 'piece', quantity: 8 });
  const b = calculate({ name: '电池B', price: 14.5, amount: 1, unit: 'piece', quantity: 7 });
  assert.ok(b.unitPrice < a.unitPrice);
  assert.equal(calculate(parse('电池 1节×8件，19.9元')).total, 8);
});

test('歧义、缺失信息、买赠和倒序包装不自动算', () => {
  for (const input of ['牛奶250ml×24盒', '牛奶250ml×24盒，59.9', '牛奶250ml 24盒，59元', '牛奶24盒×250ml，59元', '牛奶250ml×2.5盒，20元', '电池1节×6件+1件，14.5元', '牛奶250ml×24盒送2盒，59元', '牛奶250ml/200ml，59元', '', '牛奶，59元']) {
    assert.throws(() => parse(input), undefined, input);
  }
});

test('拒绝不合法价格、数量、单位和损坏的本地记录', () => {
  for (const patch of [{ price: -1 }, { price: 1.001 }, { price: Infinity }, { price: '59' }, { amount: 0 }, { amount: NaN }, { quantity: 0 }, { quantity: 1.2 }, { quantity: 1000001 }, { unit: '__proto__' }, { name: '' }, { name: 'x'.repeat(81) }]) {
    assert.throws(() => validate({ ...milk, ...patch }));
  }
  assert.throws(() => validate(null));
  assert.throws(() => parse('牛奶-250ml×24盒，59元'));
  assert.throws(() => parse('牛奶250ml×24盒，券后-59元'));
  assert.throws(() => parse('牛奶250ml×0盒，59元'));
});

test('免费的商品和非常小的单位价仍保持有效', () => {
  assert.equal(calculate({ ...milk, price: 0 }).unitPrice, 0);
  assert.ok(calculate({ ...milk, price: 0.01, amount: 1000000000, quantity: 1000000 }).unitPrice > 0);
});

test('不同单位转换一致，容量和重量保持独立', () => {
  assert.equal(calculate({ ...milk, amount: 0.25, unit: 'l' }).unitPrice, calculate(milk).unitPrice);
  assert.equal(calculate({ ...milk, unit: 'g' }).dimension, 'weight');
  assert.equal(calculate(milk).dimension, 'volume');
});
