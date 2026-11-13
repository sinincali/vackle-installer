/*
 * Vackle, a modification for Flackle's desktop app
 * Copyright (c) 2026 Xrm0s and contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;

namespace Vackle;

public static class Ed25519
{
    static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    static readonly BigInteger L = BigInteger.Pow(2, 252) + BigInteger.Parse("27742317777372353535851937790883648493");
    static readonly BigInteger D = Mod(-121665 * Inverse(121666));
    static readonly BigInteger SqrtMinusOne = BigInteger.ModPow(2, (P - 1) / 4, P);
    static readonly Point Base = BasePoint();
    static readonly byte[] SpkiPrefix = [0x30, 0x2a, 0x30, 0x05, 0x06, 0x03, 0x2b, 0x65, 0x70, 0x03, 0x21, 0x00];

    readonly record struct Point(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    public static byte[] PublicKeyFromSpki(byte[] spki)
    {
        if (spki.Length != 44 || !spki.Take(12).SequenceEqual(SpkiPrefix)) throw new FormatException("Not an Ed25519 public key");
        return spki.Skip(12).ToArray();
    }

    public static bool Verify(byte[] publicKey, byte[] message, byte[] signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64) return false;
        var a = Decode(publicKey);
        var r = Decode(signature.Take(32).ToArray());
        if (a is null || r is null) return false;
        var s = Little(signature.Skip(32).ToArray());
        if (s >= L) return false;
        byte[] digest;
        using (var sha = SHA512.Create()) digest = sha.ComputeHash(signature.Take(32).Concat(publicKey).Concat(message).ToArray());
        var k = Little(digest) % L;
        var left = Multiply(Base, s);
        var right = Add(r.Value, Multiply(a.Value, k));
        return Equal(left, right);
    }

    static Point BasePoint()
    {
        var y = Mod(4 * Inverse(5));
        var x = RecoverX(y, false) ?? throw new InvalidOperationException("Ed25519 base point");
        return new Point(x, y, 1, Mod(x * y));
    }

    static BigInteger Mod(BigInteger value)
    {
        var result = value % P;
        return result < 0 ? result + P : result;
    }

    static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);

    static BigInteger Little(byte[] bytes) => new(bytes.Concat(new byte[] { 0 }).ToArray());

    static BigInteger? RecoverX(BigInteger y, bool odd)
    {
        if (y >= P) return null;
        var x2 = Mod((y * y - 1) * Inverse(D * y * y + 1));
        if (x2 == 0) return odd ? null : BigInteger.Zero;
        var x = BigInteger.ModPow(x2, (P + 3) / 8, P);
        if (Mod(x * x - x2) != 0) x = Mod(x * SqrtMinusOne);
        if (Mod(x * x - x2) != 0) return null;
        if (x.IsEven == odd) x = P - x;
        return x;
    }

    static Point? Decode(byte[] bytes)
    {
        var copy = (byte[])bytes.Clone();
        var odd = (copy[31] & 0x80) != 0;
        copy[31] &= 0x7f;
        var y = Little(copy);
        var x = RecoverX(y, odd);
        return x is null ? null : new Point(x.Value, y, 1, Mod(x.Value * y));
    }

    static Point Add(Point p, Point q)
    {
        var a = Mod((p.Y - p.X) * (q.Y - q.X));
        var b = Mod((p.Y + p.X) * (q.Y + q.X));
        var c = Mod(2 * p.T * q.T * D);
        var d = Mod(2 * p.Z * q.Z);
        var e = b - a; var f = d - c; var g = d + c; var h = b + a;
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    static Point Multiply(Point point, BigInteger scalar)
    {
        var result = new Point(0, 1, 1, 0);
        while (scalar > 0)
        {
            if (!scalar.IsEven) result = Add(result, point);
            point = Add(point, point);
            scalar >>= 1;
        }
        return result;
    }

    static bool Equal(Point p, Point q) =>
        Mod(p.X * q.Z - q.X * p.Z) == 0 && Mod(p.Y * q.Z - q.Y * p.Z) == 0;
}
