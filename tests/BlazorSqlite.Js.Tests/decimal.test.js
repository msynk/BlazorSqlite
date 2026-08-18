import assert from 'node:assert/strict';
import { test } from 'node:test';
import { Decimal } from '../../src/BlazorSqlite/wwwroot/blazor-sqlite-decimal.js';

const values = ['0', '10', '9', '-5', '2.5', '0.1', '100', '-0.25', '1234.5678', '3'];
const optional = [null, '10', null, '-5', '2.5', null, '100', '-0.25', '1234.5678', '3'];

const parse = text => Decimal.parse(text);
const map = (xs, fn) => xs.map(x => (x === null ? null : fn(parse(x)).toString()));

test('ToString keeps the scale, including trailing zeros', () => {
  assert.equal(parse('2.5').add(parse('1.5')).toString(), '4.0');
  assert.equal(parse('0').add(parse('1.5')).toString(), '1.5');
  assert.equal(parse('0.1').multiply(parse('10')).toString(), '1.0');
  assert.equal(parse('9.0').toString(), '9.0');
  assert.equal(parse('9').toString(), '9');
  assert.equal(parse('.5').toString(), '0.5');
  assert.equal(parse('5.').toString(), '5');
  assert.equal(parse('-0').toString(), '0');
});

// The oracle is Microsoft.Data.Sqlite's `0.0###########################`, which is the form EF
// renders decimal literals in and therefore the only form its `=` comparisons match.
test('toSqlText writes the canonical form the SQLite stack stores', () => {
  assert.equal(parse('10').toSqlText(), '10.0');
  assert.equal(parse('1.50').toSqlText(), '1.5');
  assert.equal(parse('100.000').toSqlText(), '100.0');
  assert.equal(parse('0').toSqlText(), '0.0');
  assert.equal(parse('-3').toSqlText(), '-3.0');
  assert.equal(parse('12.34').toSqlText(), '12.34');
  assert.equal(parse('-0.25').toSqlText(), '-0.25');
  assert.equal(parse('1').divide(parse('3')).toSqlText(), '0.3333333333333333333333333333');
  assert.equal(parse('2.5').add(parse('1.5')).toSqlText(), '4.0');
});

test('add matches the S4 oracle', () => {
  assert.deepEqual(
    map(values, v => v.add(parse('1.5'))),
    ['1.5', '11.5', '10.5', '-3.5', '4.0', '1.6', '101.5', '1.25', '1236.0678', '4.5']);
});

test('subtract matches the S4 oracle', () => {
  assert.deepEqual(
    map(values, v => v.subtract(parse('1.5'))),
    ['-1.5', '8.5', '7.5', '-6.5', '1.0', '-1.4', '98.5', '-1.75', '1233.0678', '1.5']);
});

test('multiply matches the S4 oracle', () => {
  assert.deepEqual(
    map(values, v => v.multiply(parse('3'))),
    ['0', '30', '27', '-15', '7.5', '0.3', '300', '-0.75', '3703.7034', '9']);
});

test('divide matches the S4 oracle', () => {
  assert.deepEqual(
    map(values, v => v.divide(parse('4'))),
    ['0', '2.5', '2.25', '-1.25', '0.625', '0.025', '25', '-0.0625', '308.64195', '0.75']);
});

test('remainder matches the S4 oracle, including the sign of the dividend', () => {
  assert.deepEqual(
    map(values, v => v.remainder(parse('3'))),
    ['0', '1', '0', '-2', '2.5', '0.1', '1', '-0.25', '1.5678', '0']);
});

test('negate matches the S4 oracle and does not produce -0', () => {
  assert.deepEqual(
    map(values, v => v.negate()),
    ['0', '-10', '-9', '5', '-2.5', '-0.1', '-100', '0.25', '-1234.5678', '-3']);
});

test('1/3 is twenty-eight threes, not an IEEE float', () => {
  assert.equal(parse('1').divide(parse('3')).toString(), '0.3333333333333333333333333333');
});

test('10 / 0.04 is exact', () => {
  assert.equal(parse('10').divide(parse('0.04')).toString(), '250');
});

test('aggregates match the S4 oracle', () => {
  const sum = values.map(parse).reduce((a, b) => a.add(b));
  assert.equal(sum.toString(), '1353.9178');
  assert.equal(sum.divide(parse('10')).toString(), '135.39178');

  const present = optional.filter(v => v !== null).map(parse);
  const sumOpt = present.reduce((a, b) => a.add(b));
  assert.equal(sumOpt.toString(), '1344.8178');
  assert.equal(sumOpt.divide(new Decimal(1, BigInt(present.length), 0)).toString(),
    '192.11682857142857142857142857');

  const bucket0 = ['0', '10', '9', '-5'].map(parse).reduce((a, b) => a.add(b));
  const bucket1 = ['2.5', '0.1', '100', '-0.25', '1234.5678', '3'].map(parse).reduce((a, b) => a.add(b));
  assert.equal(bucket0.toString(), '14');
  assert.equal(bucket0.divide(parse('4')).toString(), '3.5');
  assert.equal(bucket1.toString(), '1339.9178');
  assert.equal(bucket1.divide(parse('6')).toString(), '223.31963333333333333333333333');
});

test('compare is numeric, so 9 is less than 10 and equal to 9.0', () => {
  assert.equal(Decimal.compare(parse('9'), parse('10')), -1);
  assert.equal(Decimal.compare(parse('10'), parse('9')), 1);
  assert.equal(Decimal.compare(parse('9'), parse('9.0')), 0);
  assert.equal(Decimal.compare(parse('2.5'), parse('100')), -1);
  assert.equal(Decimal.compare(parse('-5'), parse('-0.25')), -1);
  assert.equal(Decimal.compare(parse('0'), parse('-0')), 0);
});

test('order of the S4 values is numeric, not lexicographic', () => {
  const ids = values
    .map((text, i) => ({ id: i + 1, value: parse(text) }))
    .sort((a, b) => a.value.compareTo(b.value) || a.id - b.id)
    .map(row => row.id);

  assert.deepEqual(ids, [4, 8, 1, 6, 5, 10, 3, 2, 7, 9]);
});
