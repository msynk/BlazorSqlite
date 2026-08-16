// A .NET System.Decimal, in JavaScript.
//
// EF Core stores decimals as TEXT and asks the worker to add, divide, compare, and order them. IEEE
// floats cannot do that: 0.1 is already wrong, and 1/3 must come back as twenty-eight threes, not as
// 0.3333333333333333. The representation is the same one .NET uses - a 96-bit coefficient, a scale
// of 0–28, and a sign - so the strings we write are the strings EF will later compare with `=`.

const MAX_COEFF = (1n << 96n) - 1n;
const MAX_SCALE = 28;

export class DecimalOverflowError extends Error {
  constructor() {
    super('Decimal overflow.');
    this.name = 'DecimalOverflowError';
  }
}

export class DecimalFormatError extends Error {
  constructor(text) {
    super(`'${text}' is not a decimal.`);
    this.name = 'DecimalFormatError';
  }
}

export class Decimal {
  /**
   * @param {1|-1} sign
   * @param {bigint} coeff unsigned coefficient
   * @param {number} scale digits after the decimal point, 0–28
   */
  constructor(sign, coeff, scale) {
    this.sign = coeff === 0n ? 1 : sign;
    this.coeff = coeff;
    this.scale = scale;
  }

  static zero() {
    return new Decimal(1, 0n, 0);
  }

  /**
   * Parses invariant-culture decimal text. Leading and trailing whitespace and a leading sign are
   * accepted; thousands separators and exponents are not - EF never writes those.
   *
   * @param {string} text
   */
  static parse(text) {
    if (typeof text !== 'string') {
      throw new DecimalFormatError(text);
    }

    const trimmed = text.trim();
    if (!trimmed) {
      throw new DecimalFormatError(text);
    }

    let sign = 1;
    let body = trimmed;
    if (body[0] === '+' || body[0] === '-') {
      sign = body[0] === '-' ? -1 : 1;
      body = body.slice(1);
    }

    if (!body || /[^0-9.]/.test(body)) {
      throw new DecimalFormatError(text);
    }

    const dot = body.indexOf('.');
    if (dot !== body.lastIndexOf('.')) {
      throw new DecimalFormatError(text);
    }

    const digits = (dot === -1 ? body : body.slice(0, dot) + body.slice(dot + 1)).replace(/^0+(?=\d)/, '');
    const scale = dot === -1 ? 0 : body.length - dot - 1;

    if (scale > MAX_SCALE) {
      throw new DecimalFormatError(text);
    }

    if (!digits) {
      return Decimal.zero();
    }

    const coeff = BigInt(digits);
    if (coeff > MAX_COEFF) {
      throw new DecimalOverflowError();
    }

    return new Decimal(sign, coeff, scale);
  }

  /** Invariant ToString, including trailing zeros that the scale requires (`4.0`, not `4`). */
  toString() {
    const digits = this.coeff.toString();
    const sign = this.sign < 0 ? '-' : '';

    if (this.scale === 0) {
      return sign + digits;
    }

    const padded = digits.length <= this.scale
      ? digits.padStart(this.scale + 1, '0')
      : digits;
    const split = padded.length - this.scale;
    return `${sign}${padded.slice(0, split)}.${padded.slice(split)}`;
  }

  negate() {
    return this.coeff === 0n ? new Decimal(1, 0n, this.scale) : new Decimal(-this.sign, this.coeff, this.scale);
  }

  add(other) {
    if (this.coeff === 0n && this.scale <= other.scale) {
      return other;
    }

    if (other.coeff === 0n && other.scale <= this.scale) {
      return this;
    }

    const { left, right, scale } = align(this, other);

    if (this.sign === other.sign) {
      return fit(this.sign, left + right, scale);
    }

    if (left === right) {
      return new Decimal(1, 0n, scale);
    }

    if (left > right) {
      return fit(this.sign, left - right, scale);
    }

    return fit(other.sign, right - left, scale);
  }

  subtract(other) {
    return this.add(other.negate());
  }

  multiply(other) {
    const sign = this.sign * other.sign;
    return fit(sign, this.coeff * other.coeff, this.scale + other.scale);
  }

  /**
   * .NET decimal division: exact when the remainder is zero, otherwise enough digits to fill a
   * 96-bit coefficient without exceeding scale 28. Midpoints use banker's rounding.
   */
  divide(other) {
    if (other.coeff === 0n) {
      throw new Error('Division by zero.');
    }

    if (this.coeff === 0n) {
      return Decimal.zero();
    }

    const sign = /** @type {1|-1} */ (this.sign * other.sign);
    let quotient = this.coeff / other.coeff;
    let remainder = this.coeff % other.coeff;
    let extra = 0;

    const resultScale = () => extra + this.scale - other.scale;
    const canGrow = () => quotient <= MAX_COEFF / 10n;

    while (remainder !== 0n && canGrow() && resultScale() < MAX_SCALE) {
      remainder *= 10n;
      quotient = quotient * 10n + remainder / other.coeff;
      remainder = remainder % other.coeff;
      extra++;
    }

    if (remainder !== 0n) {
      const twice = remainder * 2n;
      const roundUp = twice > other.coeff || (twice === other.coeff && (quotient & 1n) === 1n);
      if (roundUp) {
        quotient += 1n;
      }
    }

    let scale = resultScale();

    if (remainder === 0n) {
      while (scale < 0) {
        if (quotient > MAX_COEFF / 10n) {
          throw new DecimalOverflowError();
        }

        quotient *= 10n;
        scale++;
      }
    } else if (scale < 0) {
      throw new DecimalOverflowError();
    }

    return fit(sign, quotient, scale);
  }

  /**
   * Remainder with the sign of the dividend, truncated toward zero - `a - Truncate(a/b)*b`.
   */
  remainder(other) {
    if (other.coeff === 0n) {
      throw new Error('Division by zero.');
    }

    return this.subtract(this.divide(other).truncate().multiply(other));
  }

  truncate() {
    if (this.scale === 0) {
      return this;
    }

    return new Decimal(this.sign, this.coeff / 10n ** BigInt(this.scale), 0);
  }

  /**
   * Numeric comparison, ignoring scale. `9` and `9.0` compare equal, which is what `ef_compare`
   * and the `EF_DECIMAL` collation both need.
   *
   * @returns {-1|0|1}
   */
  compareTo(other) {
    if (this.coeff === 0n && other.coeff === 0n) {
      return 0;
    }

    if (this.sign !== other.sign) {
      return this.sign < other.sign ? -1 : 1;
    }

    const { left, right } = align(this, other);
    if (left === right) {
      return 0;
    }

    const magnitude = left < right ? -1 : 1;
    return /** @type {-1|0|1} */ (this.sign < 0 ? -magnitude : magnitude);
  }

  static compare(left, right) {
    return left.compareTo(right);
  }
}

/**
 * @param {Decimal} a
 * @param {Decimal} b
 */
function align(a, b) {
  const scale = Math.max(a.scale, b.scale);
  return {
    left: a.coeff * 10n ** BigInt(scale - a.scale),
    right: b.coeff * 10n ** BigInt(scale - b.scale),
    scale,
  };
}

/**
 * Fits a coefficient into 96 bits and a scale of 0–28, rounding with banker's rounding when a
 * digit has to be dropped.
 *
 * @param {1|-1} sign
 * @param {bigint} coeff
 * @param {number} scale
 */
function fit(sign, coeff, scale) {
  if (coeff < 0n) {
    throw new Error('fit() expects an unsigned coefficient.');
  }

  while (coeff > MAX_COEFF || scale > MAX_SCALE) {
    if (scale === 0 && coeff > MAX_COEFF) {
      throw new DecimalOverflowError();
    }

    const digit = coeff % 10n;
    coeff /= 10n;
    scale--;

    if (digit > 5n || (digit === 5n && (coeff & 1n) === 1n)) {
      coeff += 1n;
    }
  }

  if (scale < 0) {
    throw new DecimalOverflowError();
  }

  return new Decimal(sign, coeff, scale);
}
