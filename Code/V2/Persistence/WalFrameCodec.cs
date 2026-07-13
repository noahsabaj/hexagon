#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hexagon.V2.Persistence;

internal sealed record WalDecodeResult(
	IReadOnlyList<WalCommitBatch> Batches,
	int ValidLength,
	bool HasPartialTail );

/// <summary>
/// WAL framing: magic (4), framing version (1), payload length (4 LE), SHA-256 (32), JSON payload.
/// A truncated final header or payload is recoverable. Invalid magic, checksum, or JSON is corruption.
/// </summary>
internal static class WalFrameCodec
{
	private static ReadOnlySpan<byte> Magic => "HXW2"u8;
	private const byte FramingVersion = 1;
	private const int HeaderLength = 4 + 1 + 4 + 32;
	private const int MaximumPayloadLength = 64 * 1024 * 1024;

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = false,
		WriteIndented = false
	};

	public static byte[] Encode( WalCommitBatch batch )
	{
		ArgumentNullException.ThrowIfNull( batch );
		var payload = JsonSerializer.SerializeToUtf8Bytes( batch, JsonOptions );
		if ( payload.Length > MaximumPayloadLength )
		{
			throw new InvalidOperationException( $"WAL payload exceeds {MaximumPayloadLength} bytes." );
		}

		var frame = new byte[HeaderLength + payload.Length];
		Magic.CopyTo( frame );
		frame[4] = FramingVersion;
		BinaryPrimitives.WriteInt32LittleEndian( frame.AsSpan( 5, 4 ), payload.Length );
		SHA256.HashData( payload ).CopyTo( frame, 9 );
		payload.CopyTo( frame, HeaderLength );
		return frame;
	}

	public static WalDecodeResult Decode( ReadOnlySpan<byte> bytes )
	{
		var batches = new List<WalCommitBatch>();
		var offset = 0;
		Span<byte> actualHash = stackalloc byte[32];

		while ( offset < bytes.Length )
		{
			var remaining = bytes.Length - offset;
			if ( remaining < HeaderLength )
			{
				return new WalDecodeResult( batches, offset, true );
			}

			var header = bytes.Slice( offset, HeaderLength );
			if ( !header[..4].SequenceEqual( Magic ) )
			{
				throw new PersistenceCorruptionException( $"Invalid WAL magic at byte offset {offset}." );
			}

			if ( header[4] != FramingVersion )
			{
				throw new PersistenceCorruptionException( $"Unsupported WAL framing version {header[4]} at byte offset {offset}." );
			}

			var payloadLength = BinaryPrimitives.ReadInt32LittleEndian( header.Slice( 5, 4 ) );
			if ( payloadLength < 0 || payloadLength > MaximumPayloadLength )
			{
				throw new PersistenceCorruptionException( $"Invalid WAL payload length {payloadLength} at byte offset {offset}." );
			}

			if ( remaining - HeaderLength < payloadLength )
			{
				return new WalDecodeResult( batches, offset, true );
			}

			var payload = bytes.Slice( offset + HeaderLength, payloadLength );
			SHA256.HashData( payload, actualHash );
			if ( !HashesEqual( actualHash, header.Slice( 9, 32 ) ) )
			{
				throw new PersistenceCorruptionException( $"WAL checksum mismatch at byte offset {offset}." );
			}

			try
			{
				var batch = JsonSerializer.Deserialize<WalCommitBatch>( payload, JsonOptions )
					?? throw new JsonException( "WAL frame contains null JSON." );
				ValidateBatch( batch, offset );
				batches.Add( batch );
			}
			catch ( PersistenceCorruptionException )
			{
				throw;
			}
			catch ( Exception exception ) when ( exception is JsonException or NotSupportedException )
			{
				throw new PersistenceCorruptionException( $"Invalid WAL JSON at byte offset {offset}.", exception );
			}

			offset += HeaderLength + payloadLength;
		}

		return new WalDecodeResult( batches, offset, false );
	}

	private static bool HashesEqual( ReadOnlySpan<byte> left, ReadOnlySpan<byte> right )
	{
		if ( left.Length != right.Length )
		{
			return false;
		}

		var difference = 0;
		for ( var index = 0; index < left.Length; index++ )
		{
			difference |= left[index] ^ right[index];
		}

		return difference == 0;
	}

	private static void ValidateBatch( WalCommitBatch batch, int offset )
	{
		if ( batch.FormatVersion != WalCommitBatch.CurrentFormatVersion )
		{
			throw new PersistenceCorruptionException(
				$"Unsupported WAL commit format {batch.FormatVersion} at byte offset {offset}." );
		}

		if ( batch.Sequence <= 0 )
		{
			throw new PersistenceCorruptionException( $"Invalid WAL sequence {batch.Sequence} at byte offset {offset}." );
		}

		if ( batch.Mutations is null || batch.Mutations.Count == 0 )
		{
			throw new PersistenceCorruptionException( $"Empty WAL commit at byte offset {offset}." );
		}
	}
}
