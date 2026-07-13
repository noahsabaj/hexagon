#nullable enable

using Hexagon.V2.Domain;
using Hexagon.V2.Kernel.Schema;

namespace Hexagon.V2.Application;

public sealed class SchemaItemShapeCatalog : IItemShapeCatalog
{
	private readonly CompiledSchema _schema;
	private readonly DomainRepositories _repositories;

	public SchemaItemShapeCatalog( CompiledSchema schema, DomainRepositories repositories )
	{
		_schema = schema ?? throw new System.ArgumentNullException( nameof(schema) );
		_repositories = repositories ?? throw new System.ArgumentNullException( nameof(repositories) );
	}

	public bool TryGetShape( DefinitionId definitionId, out ItemShape shape )
	{
		if ( _schema.Items.TryGet( definitionId.Value, out var definition ) )
		{
			shape = new ItemShape( definition!.Width, definition.Height );
			return true;
		}

		shape = default;
		return false;
	}

	public bool TryGetShape( ItemId itemId, out ItemShape shape )
	{
		var item = _repositories.Items.Find( DomainKeys.Item( itemId ) );
		if ( item is not null ) return TryGetShape( item.Value.Definition, out shape );
		shape = default;
		return false;
	}
}
