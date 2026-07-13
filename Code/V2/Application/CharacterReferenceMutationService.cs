#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hexagon.V2.Domain;
using Hexagon.V2.Kernel;
using Hexagon.V2.Persistence;

namespace Hexagon.V2.Application;

/// <summary>
/// Sole mutation boundary for searchable character references. Every old and new character target
/// is fenced by its lifecycle guard in the same transaction, making delete/reference races conflict.
/// </summary>
public sealed class CharacterReferenceMutationService
{
	private readonly DomainRepositories _repositories;

	public CharacterReferenceMutationService( DomainRepositories repositories ) =>
		_repositories = repositories ?? throw new ArgumentNullException( nameof(repositories) );

	public async ValueTask<OperationResult<CommitReceipt>> UpsertAsync(
		string key,
		CharacterReferenceRecord reference,
		CancellationToken cancellationToken = default )
	{
		ArgumentNullException.ThrowIfNull( reference );
		var unit = _repositories.Provider.BeginUnitOfWork();
		var staged = StageUpsert( unit, key, reference );
		if ( staged.Failed )
		{
			await unit.DisposeAsync();
			return OperationResult<CommitReceipt>.Failure( staged.Error!.Code, staged.Error.Message );
		}

		var committed = await unit.CommitAsync( cancellationToken );
		await unit.DisposeAsync();
		return committed.Succeeded
			? OperationResult<CommitReceipt>.Success( committed.Value! )
			: OperationResult<CommitReceipt>.Failure(
				PersistenceResultMapping.MapCode( committed.Error!.Code ), committed.Error.Message );
	}

	public async ValueTask<OperationResult<CommitReceipt>> DeleteAsync(
		string key,
		CancellationToken cancellationToken = default )
	{
		var unit = _repositories.Provider.BeginUnitOfWork();
		var staged = StageDelete( unit, key );
		if ( staged.Failed )
		{
			await unit.DisposeAsync();
			return OperationResult<CommitReceipt>.Failure( staged.Error!.Code, staged.Error.Message );
		}
		var committed = await unit.CommitAsync( cancellationToken );
		await unit.DisposeAsync();
		return committed.Succeeded
			? OperationResult<CommitReceipt>.Success( committed.Value! )
			: OperationResult<CommitReceipt>.Failure(
				PersistenceResultMapping.MapCode( committed.Error!.Code ), committed.Error.Message );
	}

	public OperationResult StageUpsert(
		IUnitOfWork unit,
		string key,
		CharacterReferenceRecord reference )
	{
		ArgumentNullException.ThrowIfNull( unit );
		ArgumentNullException.ThrowIfNull( reference );
		var existing = _repositories.CharacterReferences.Find( key );
		var fenced = FenceTargets( unit, Targets( existing?.Value, reference ) );
		if ( fenced.Failed ) return fenced;
		if ( existing is null ) unit.Create( _repositories.CharacterReferences, key, reference );
		else
		{
			var editor = unit.Edit( _repositories.CharacterReferences, existing );
			if ( editor is null )
				return OperationResult.Failure( ErrorCode.Conflict, "Character reference changed before mutation." );
			editor.Replace( reference );
			unit.Save( editor );
		}
		return OperationResult.Success();
	}

	public OperationResult StageDelete( IUnitOfWork unit, string key )
	{
		ArgumentNullException.ThrowIfNull( unit );
		var existing = _repositories.CharacterReferences.Find( key );
		if ( existing is null ) return OperationResult.Failure( ErrorCode.NotFound, "Character reference was not found." );
		var fenced = FenceTargets( unit, Targets( existing.Value, null ) );
		if ( fenced.Failed ) return fenced;
		unit.Delete( _repositories.CharacterReferences, existing );
		return OperationResult.Success();
	}

	private OperationResult FenceTargets( IUnitOfWork unit, IReadOnlySet<CharacterId> targets )
	{
		foreach ( var target in targets )
		{
			var document = _repositories.CharacterLifecycleGuards.Find(
				DomainKeys.CharacterLifecycleGuard( target ) );
			if ( document is null )
				return OperationResult.Failure( ErrorCode.NotFound, $"Character lifecycle guard '{target}' is missing." );
			var editor = unit.Edit( _repositories.CharacterLifecycleGuards, document );
			if ( editor is null )
				return OperationResult.Failure( ErrorCode.Conflict, $"Character lifecycle guard '{target}' changed." );
			editor.Replace( editor.Value with { ReferenceRevision = checked(editor.Value.ReferenceRevision + 1) } );
			unit.Save( editor );
		}
		return OperationResult.Success();
	}

	private static IReadOnlySet<CharacterId> Targets(
		CharacterReferenceRecord? previous,
		CharacterReferenceRecord? next )
	{
		var result = new HashSet<CharacterId>();
		Add( previous );
		Add( next );
		return result;

		void Add( CharacterReferenceRecord? value )
		{
			if ( value is null ) return;
			result.Add( value.CharacterId );
			if ( value.RelatedCharacterId is { } related ) result.Add( related );
		}
	}
}
