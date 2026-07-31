#nullable enable

using System;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;

namespace Hexagon.V2.Application;

public static class CurrencyService
{
	public static OperationResult<CharacterRecord> Credit( CharacterRecord character, long amount )
	{
		if ( amount < 0 ) return OperationResult<CharacterRecord>.Failure( ErrorCode.InvalidArgument, "Credit amount cannot be negative." );
		try
		{
			return OperationResult<CharacterRecord>.Success( character.WithBalance( checked(character.Balance + amount) ) );
		}
		catch ( OverflowException )
		{
			return OperationResult<CharacterRecord>.Failure( ErrorCode.Conflict, "Balance would overflow." );
		}
	}

	public static OperationResult<CharacterRecord> Debit( CharacterRecord character, long amount )
	{
		if ( amount < 0 ) return OperationResult<CharacterRecord>.Failure( ErrorCode.InvalidArgument, "Debit amount cannot be negative." );
		if ( character.Balance < amount ) return OperationResult<CharacterRecord>.Failure( ErrorCode.Conflict, "Insufficient balance." );
		return OperationResult<CharacterRecord>.Success( character.WithBalance( character.Balance - amount ) );
	}
}
