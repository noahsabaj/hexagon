#nullable enable

using Hexagon.V2.Kernel;
using Hexagon.V2.Persistence;

namespace Hexagon.V2.Application;

internal static class PersistenceResultMapping
{
	public static OperationResult<T> Failure<T>( PersistenceError error ) =>
		OperationResult<T>.Failure( Map( error.Code ), error.Message );

	public static OperationResult Failure( PersistenceError error ) =>
		OperationResult.Failure( Map( error.Code ), error.Message );

	private static ErrorCode Map( PersistenceErrorCode code ) => code switch
	{
		PersistenceErrorCode.NotFound => ErrorCode.NotFound,
		PersistenceErrorCode.AlreadyExists => ErrorCode.Conflict,
		PersistenceErrorCode.RevisionConflict => ErrorCode.Conflict,
		PersistenceErrorCode.TypeNotRegistered => ErrorCode.PersistedTypeInvalid,
		PersistenceErrorCode.CollectionTypeMismatch => ErrorCode.PersistedTypeInvalid,
		PersistenceErrorCode.InvalidOperation => ErrorCode.InvalidArgument,
		_ => ErrorCode.InternalError
	};
}
