import { expect, test } from '@playwright/test';
import { exec, openHost, query } from './host.js';

/**
 * The S4 data set: values chosen so lexicographic TEXT order and numeric order disagree
 * (`9` vs `10`, `2.5` vs `100`), with zero, negatives, sub-unit scales, and nulls.
 */
const ROWS = [
  { id: 1, value: '0', optional: null, bucket: 0, text: 'alpha' },
  { id: 2, value: '10', optional: '10', bucket: 0, text: 'Alpha' },
  { id: 3, value: '9', optional: null, bucket: 0, text: 'beta-1' },
  { id: 4, value: '-5', optional: '-5', bucket: 0, text: 'gamma 42' },
  { id: 5, value: '2.5', optional: '2.5', bucket: 1, text: '' },
  { id: 6, value: '0.1', optional: null, bucket: 1, text: 'a.b.c' },
  { id: 7, value: '100', optional: '100', bucket: 1, text: '123' },
  { id: 8, value: '-0.25', optional: '-0.25', bucket: 1, text: 'x[y]z' },
  { id: 9, value: '1234.5678', optional: '1234.5678', bucket: 1, text: '  spaced  ' },
  { id: 10, value: '3', optional: '3', bucket: 1, text: 'delta\ntail' },
];

test.beforeEach(async ({ page }) => {
  await openHost(page, { databaseName: 'functions.db' });
  await seed(page);
});

test.describe('scalar decimal functions', () => {
  const cases = [
    ['add', "ef_add(value, '1.5')",
      ['1.5', '11.5', '10.5', '-3.5', '4.0', '1.6', '101.5', '1.25', '1236.0678', '4.5']],
    ['subtract', "ef_add(value, ef_negate('1.5'))",
      ['-1.5', '8.5', '7.5', '-6.5', '1.0', '-1.4', '98.5', '-1.75', '1233.0678', '1.5']],
    ['multiply', "ef_multiply(value, '3')",
      ['0', '30', '27', '-15', '7.5', '0.3', '300', '-0.75', '3703.7034', '9']],
    ['divide', "ef_divide(value, '4')",
      ['0', '2.5', '2.25', '-1.25', '0.625', '0.025', '25', '-0.0625', '308.64195', '0.75']],
    ['modulo', "ef_mod(value, '3')",
      ['0', '1', '0', '-2', '2.5', '0.1', '1', '-0.25', '1.5678', '0']],
    ['negate', 'ef_negate(value)',
      ['0', '-10', '-9', '5', '-2.5', '-0.1', '-100', '0.25', '-1234.5678', '-3']],
    ['nullable add', "ef_add(optional, '1')",
      [null, '11', null, '-4', '3.5', null, '101', '0.75', '1235.5678', '4']],
  ];

  for (const [name, expr, expected] of cases) {
    test(name, async ({ page }) => {
      const result = await query(page, `SELECT ${expr} FROM rows ORDER BY id`);
      expect(result.rows.map(row => row[0])).toEqual(expected);
    });
  }

  test('1/3 is twenty-eight threes, not an IEEE float', async ({ page }) => {
    const result = await query(page, "SELECT ef_divide('1', '3')");
    expect(result.rows).toEqual([['0.3333333333333333333333333333']]);
  });

  test('a zero divisor yields null rather than an error', async ({ page }) => {
    const result = await query(page, "SELECT ef_divide('10', '0'), ef_mod('10', '0')");
    expect(result.rows).toEqual([[null, null]]);
  });
});

test.describe('aggregates', () => {
  test('sum, average, max, min', async ({ page }) => {
    const result = await query(page,
      'SELECT ef_sum(value), ef_avg(value), ef_max(value), ef_min(value) FROM rows');

    expect(result.rows).toEqual([['1353.9178', '135.39178', '1234.5678', '-5']]);
  });

  test('nullable aggregates skip nulls', async ({ page }) => {
    const result = await query(page,
      'SELECT ef_sum(optional), ef_avg(optional), ef_max(optional), ef_min(optional) FROM rows');

    expect(result.rows).toEqual([[
      '1344.8178',
      '192.11682857142857142857142857',
      '1234.5678',
      '-5',
    ]]);
  });

  test('grouped sum and average', async ({ page }) => {
    const result = await query(page,
      'SELECT bucket, ef_sum(value), ef_avg(value) FROM rows GROUP BY bucket ORDER BY bucket');

    expect(result.rows).toEqual([
      [0, '14', '3.5'],
      [1, '1339.9178', '223.31963333333333333333333333'],
    ]);
  });

  test('an empty group yields null, not zero', async ({ page }) => {
    const result = await query(page, "SELECT ef_sum(value), ef_avg(value) FROM rows WHERE id = 0");
    expect(result.rows).toEqual([[null, null]]);
  });
});

test.describe('ordering and comparison', () => {
  test('ORDER BY COLLATE EF_DECIMAL is numeric, not lexicographic', async ({ page }) => {
    const result = await query(page, 'SELECT id FROM rows ORDER BY value COLLATE EF_DECIMAL, id');
    expect(column(result)).toEqual([4, 8, 1, 6, 5, 10, 3, 2, 7, 9]);
  });

  test('ORDER BY DESC is the reverse numeric order', async ({ page }) => {
    const result = await query(page,
      'SELECT id FROM rows ORDER BY value COLLATE EF_DECIMAL DESC, id');
    expect(column(result)).toEqual([9, 7, 2, 3, 10, 5, 6, 1, 8, 4]);
  });

  test('nullable ORDER BY puts SQLite nulls first, then numeric order', async ({ page }) => {
    const result = await query(page,
      'SELECT id FROM rows ORDER BY optional COLLATE EF_DECIMAL, id');
    expect(column(result)).toEqual([1, 3, 6, 4, 8, 5, 10, 2, 7, 9]);
  });

  test('without the collation, 9 sorts after 100', async ({ page }) => {
    // The failure this prevents: a missing collation looks like a working ORDER BY until the
    // data set contains values whose TEXT order and numeric order disagree.
    const result = await query(page, "SELECT value FROM rows WHERE value IN ('9', '100') ORDER BY value");
    expect(column(result)).toEqual(['100', '9']);
  });

  const filters = [
    ['greater than', "ef_compare(value, '2.5') > 0", [2, 3, 7, 9, 10]],
    ['greater or equal', "ef_compare(value, '2.5') >= 0", [2, 3, 5, 7, 9, 10]],
    ['less than', "ef_compare(value, '2.5') < 0", [1, 4, 6, 8]],
    ['less than zero', "ef_compare(value, '0') < 0", [4, 8]],
    ['equals via compare', "ef_compare(value, '9') = 0", [3]],
    ['not equals via compare', "ef_compare(value, '9') <> 0", [1, 2, 4, 5, 6, 7, 8, 9, 10]],
    ['null check', 'optional IS NULL', [1, 3, 6]],
    ['computed filter', "ef_compare(ef_multiply(value, '2'), '10') > 0", [2, 3, 7, 9]],
  ];

  for (const [name, where, expected] of filters) {
    test(name, async ({ page }) => {
      const result = await query(page, `SELECT id FROM rows WHERE ${where} ORDER BY id`);
      expect(column(result)).toEqual(expected);
    });
  }

  test('TEXT equality uses the stored form, so 9 equals 9 and not 9.0', async ({ page }) => {
    // EF compiles `== 9m` to a TEXT compare against the canonical string. Getting ToString wrong
    // would make a lookup for a value we just wrote miss.
    expect(column(await query(page, "SELECT id FROM rows WHERE value = '9'"))).toEqual([3]);
    expect(column(await query(page, "SELECT id FROM rows WHERE value = '9.0'"))).toEqual([]);
    expect(column(await query(page, "SELECT id FROM rows WHERE ef_compare(value, '9.0') = 0")))
      .toEqual([3]);
  });
});

test.describe('regexp', () => {
  const patterns = [
    ['^a', [1, 6]],
    ['^A', [2]],
    ['a', [1, 2, 3, 4, 6, 9, 10]],
    ['[0-9]+', [3, 4, 7]],
    ['^[0-9]+$', [7]],
    ['\\d', [3, 4, 7]],
    ['a\\.b', [6]],
    ['\\[y\\]', [8]],
    ['^$', [5]],
    ['^\\s+', [9]],
    ['tail$', [10]],
    ['(alpha|beta)', [1, 3]],
    ['z{1}', [8]],
    ['^.$', []],
    ['a(?=l)', [1]],
    ['a(?!l)', [1, 2, 3, 4, 6, 9, 10]],
    ['(\\w)\\1', [4]],
  ];

  for (const [pattern, expected] of patterns) {
    test(`REGEXP ${pattern}`, async ({ page }) => {
      const result = await query(
        page,
        `SELECT id FROM rows WHERE text REGEXP ${sqlQuote(pattern)} ORDER BY id`);
      expect(column(result)).toEqual(expected);
    });
  }

  test('regexp(pattern, input) is the argument order SQLite uses', async ({ page }) => {
    // A transposition would pass '^a' against 'alpha' if someone called IsMatch-style
    // regexp(input, pattern) and the test only used the operator.
    const result = await query(page, "SELECT regexp('^a', 'alpha'), regexp('alpha', '^a')");
    expect(result.rows).toEqual([[1, 0]]);
  });

  test('a null operand yields null', async ({ page }) => {
    const result = await query(page, 'SELECT regexp(NULL, text), text REGEXP NULL FROM rows WHERE id = 1');
    expect(result.rows).toEqual([[null, null]]);
  });
});

async function seed(page) {
  await exec(page, `
    CREATE TABLE rows (
      id INTEGER PRIMARY KEY,
      value TEXT NOT NULL,
      optional TEXT,
      bucket INTEGER NOT NULL,
      text TEXT NOT NULL
    )`);

  for (const row of ROWS) {
    await exec(
      page,
      'INSERT INTO rows (id, value, optional, bucket, text) VALUES (@id, @value, @optional, @bucket, @text)',
      [
        { name: '@id', value: row.id },
        { name: '@value', value: row.value },
        { name: '@optional', value: row.optional },
        { name: '@bucket', value: row.bucket },
        { name: '@text', value: row.text },
      ]);
  }
}

function column(result) {
  return result.rows.map(row => row[0]);
}

function sqlQuote(value) {
  return `'${String(value).replaceAll("'", "''")}'`;
}
