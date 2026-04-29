using Umbraco.Cms.Core.Models;

namespace TINUmbraco.Web.Migrations;

public interface IWordPressMigrationPropertyMapper
{
    void ApplyValues(IContent content, IReadOnlyDictionary<string, object?> values, string wordPressType, bool dryRun);
}