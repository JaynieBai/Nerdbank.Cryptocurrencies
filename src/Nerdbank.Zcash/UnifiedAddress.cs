﻿// Copyright (c) Andrew Arnott. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Collections.ObjectModel;
using System.Diagnostics;
using Konscious.Security.Cryptography;
using Microsoft;

namespace Nerdbank.Zcash;

/// <summary>
/// A <see href="https://zips.z.cash/zip-0316">unified Zcash address</see>.
/// </summary>
public class UnifiedAddress : ZcashAddress
{
    private const string HumanReadablePart = "u";

    private const int MinF4JumbleInputLength = 48;
    private const int MaxF4JumbleInputLength = 4194368;
    private const int F4OutputLength = 64; // known in the spec as ℒᵢ

    private ReadOnlyCollection<ZcashAddress>? receivers;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnifiedAddress"/> class.
    /// </summary>
    /// <param name="address"><inheritdoc cref="ZcashAddress.ZcashAddress(ReadOnlySpan{char})" path="/param"/></param>
    internal UnifiedAddress(ReadOnlySpan<char> address)
        : base(address)
    {
    }

    /// <inheritdoc/>
    public override ZcashNetwork Network => throw new NotImplementedException();

    /// <summary>
    /// Gets the receivers for this address, in order of preference.
    /// </summary>
    /// <remarks>
    /// <para>Every address has at least one receiver, if it is valid. A <see cref="UnifiedAddress"/> in this sequence should be interpreted as an Orchard raw receiver.</para>
    /// </remarks>
    public IReadOnlyList<ZcashAddress> Receivers => this.receivers ??= this.GetReceivers();

    /// <summary>
    /// Gets a value indicating whether this address is a raw Orchard address rather than an address with receivers.
    /// </summary>
    internal bool IsOrchardRaw => throw new NotImplementedException();

    /// <inheritdoc/>
    internal override byte UnifiedAddressTypeCode => 0x03;

    /// <inheritdoc/>
    private protected override int ReceiverEncodingLength => throw new NotImplementedException();

    /// <summary>
    /// Gets the padding bytes that must be present in a unified address.
    /// </summary>
    private static ReadOnlySpan<byte> Padding => "u\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0"u8;

    /// <summary>
    /// Creates a unified address from a list of receiver addresses.
    /// </summary>
    /// <param name="receivers">
    /// The receivers to build into the unified address.
    /// These will be sorted by preferred order before being encoded into the address.
    /// No more than one of each type of address is allowed.
    /// Sprout addresses are not allowed.
    /// </param>
    /// <returns>A unified address that contains all the receivers.</returns>
    public static UnifiedAddress Create(IReadOnlyCollection<ZcashAddress> receivers)
    {
        SortedDictionary<byte, ZcashAddress> sortedReceiversByTypeCode = new();
        int totalLength = 0;

        bool hasShieldedAddress = false;
        foreach (ZcashAddress receiver in receivers)
        {
            hasShieldedAddress |= receiver.UnifiedAddressTypeCode > 0x01;
            if (sortedReceiversByTypeCode.TryAdd(receiver.UnifiedAddressTypeCode, receiver))
            {
                throw new ArgumentException($"Only one of each type of address is allowed, but more than one {receiver.GetType().Name} was specified.", nameof(receivers));
            }

            totalLength += receiver.UAContributionLength;
        }

        Requires.Argument(hasShieldedAddress, nameof(receivers), "At least one shielded address is required.");

        totalLength += Padding.Length;
        Span<byte> ua = stackalloc byte[totalLength];
        int uaBytesWritten = 0;
        foreach (ZcashAddress receiver in sortedReceiversByTypeCode.Values)
        {
            uaBytesWritten += receiver.GetReceiverEncoding(ua.Slice(uaBytesWritten));
        }

        Padding.CopyTo(ua.Slice(uaBytesWritten));
        uaBytesWritten += Padding.Length;
        F4Jumble(ua);

        Assumes.True(uaBytesWritten == ua.Length);

        Span<char> result = stackalloc char[Bech32.GetEncodedLength(HumanReadablePart.Length, ua.Length)];
        int finalLength = Bech32.Bech32m.Encode(HumanReadablePart, ua, result);
        Assumes.True(result.Length == finalLength);
        return new(result);
    }

    /// <inheritdoc/>
    public override bool SupportsPool(Pool pool)
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Decodes the address to its raw encoding.
    /// </summary>
    /// <param name="rawEncoding">Receives the raw encoding of the data within the address.</param>
    /// <returns>The number of bytes written to <paramref name="rawEncoding"/>.</returns>
    /// <exception cref="FormatException">Thrown if the address is invalid.</exception>
    internal int Decode(Span<byte> rawEncoding) => throw new NotImplementedException();

    /// <inheritdoc/>
    internal override int GetReceiverEncoding(Span<byte> destination)
    {
        Verify.Operation(this.IsOrchardRaw, "This is a combined UA.");
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    protected override bool CheckValidity(bool throwIfInvalid = false)
    {
        (int Tag, int Data)? length = Bech32.GetDecodedLength(this.Address);
        if (length is null)
        {
            return false;
        }

        Span<char> tag = stackalloc char[length.Value.Tag];
        Span<byte> data = stackalloc byte[length.Value.Data];
        return Bech32.Bech32m.TryDecode(this.Address, tag, data, out _, out _, out _);
    }

    /// <summary>
    /// Applies the F4Jumble function to the specified buffer.
    /// </summary>
    /// <param name="ua">The buffer to mutate.</param>
    /// <param name="inverted"><see langword="true" /> to reverse the process.</param>
    /// <exception cref="ArgumentException">Thrown when the input buffer is shorter than <see cref="MinF4JumbleInputLength"/> or longer than <see cref="MaxF4JumbleInputLength"/>.</exception>
    /// <devremarks>
    /// <see href="https://docs.rs/f4jumble/latest/src/f4jumble/lib.rs.html#208">Some source for inspiration</see> while interpreting the spec.
    /// </devremarks>
    private static void F4Jumble(Span<byte> ua, bool inverted = false)
    {
        if (ua.Length is < MinF4JumbleInputLength or > MaxF4JumbleInputLength)
        {
            throw new ArgumentException($"The UA cannot exceed {MaxF4JumbleInputLength} bytes.", nameof(ua));
        }

        byte[] arrayBuffer = new byte[ua.Length];
        ua.CopyTo(arrayBuffer);

        byte leftLength = (byte)Math.Min(F4OutputLength, ua.Length / 2);
        int rightLength = ua.Length - leftLength;

        if (inverted)
        {
            RoundH(1);
            RoundG(1);
            RoundH(0);
            RoundG(0);
        }
        else
        {
            RoundG(0);
            RoundH(0);
            RoundG(1);
            RoundH(1);
        }

        arrayBuffer.CopyTo(ua);

        // TODO: Several opportunities below to reduce allocations.

        void RoundG(byte i)
        {
            ushort top = checked((ushort)CeilDiv(rightLength, F4OutputLength));
            for (ushort j = 0; j < top; j++)
            {
                using HMACBlake2B blake2 = new(PersonalizeG(i, j), F4OutputLength);
                byte[] hash = blake2.ComputeHash(arrayBuffer, 0, leftLength);
                Xor(arrayBuffer.AsSpan(j * F4OutputLength), hash);
            }
        }

        void RoundH(byte i)
        {
            using HMACBlake2B blake2 = new(PersonalizeH(i), leftLength);
            byte[] hash = blake2.ComputeHash(arrayBuffer, leftLength, rightLength);
            Xor(arrayBuffer.AsSpan(0, leftLength), hash);
        }

        static byte[] PersonalizeH(byte i) => new byte[] { 85, 65, 95, 70, 52, 74, 117, 109, 98, 108, 101, 95, 72, i, 0, 0 };

        static byte[] PersonalizeG(byte i, ushort j) => new byte[] { 85, 65, 95, 70, 52, 74, 117, 109, 98, 108, 101, 95, 71, i, (byte)(j & 0xff), (byte)(j >> 8) };

        static int CeilDiv(int number, int divisor) => (number + divisor - 1) / divisor;

        static void Xor(Span<byte> left, ReadOnlySpan<byte> right)
        {
            Debug.Assert(left.Length == right.Length, "Buffer lengths must equal.");
            for (int i = 0; i < left.Length; i++)
            {
                left[i] ^= right[i];
            }
        }
    }

    private ReadOnlyCollection<ZcashAddress> GetReceivers()
    {
        (int Tag, int Data) bech32DecodedLength = Bech32.GetDecodedLength(this.Address) ?? throw new InvalidAddressException();

        if (bech32DecodedLength.Data is < MinF4JumbleInputLength or > MaxF4JumbleInputLength)
        {
            throw new InvalidAddressException();
        }

        Span<char> tag = stackalloc char[bech32DecodedLength.Tag];
        Span<byte> data = stackalloc byte[bech32DecodedLength.Data];
        Bech32.Bech32m.Decode(this.Address, tag, data);

        // Verify the 16-byte padding is as expected, then strip it.
        if (!data.Slice(data.Length - Padding.Length).SequenceEqual(Padding))
        {
            throw new InvalidAddressException();
        }

        data = data.Slice(0, data.Length - Padding.Length);

        throw new NotImplementedException();
    }
}
