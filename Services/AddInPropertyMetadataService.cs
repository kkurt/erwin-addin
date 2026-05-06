using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using EliteSoft.MetaAdmin.Shared.Data;
using EliteSoft.MetaAdmin.Shared.Data.Entities;
using EliteSoft.MetaAdmin.Shared.Services;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// Lightweight IPropertyMetadataService implementation for the add-in.
    /// Read-only: only loads platforms, object types, property defs, enum options, and project standards.
    /// No seed data or write operations (those are handled by erwin-admin).
    /// </summary>
    public class AddInPropertyMetadataService : IPropertyMetadataService
    {
        private readonly IBootstrapService _bootstrapService;

        // Static MC table - rows change only when an admin edits the schema, which
        // never happens during a single addin session. Cost was ~1600ms on the first
        // hit (EF cold-start) and an MSSQL round-trip on each subsequent hit; this
        // process-lifetime cache turns repeated reads into a HashSet lookup.
        private static List<ObjectType> _objectTypesCache;
        private static readonly object _objectTypesGate = new();

        public AddInPropertyMetadataService(IBootstrapService bootstrapService)
        {
            _bootstrapService = bootstrapService ?? throw new ArgumentNullException(nameof(bootstrapService));
        }

        private RepoDbContext CreateContext()
        {
            var config = _bootstrapService.GetConfig();
            if (config == null || !config.IsConfigured)
                throw new InvalidOperationException("Database not configured");
            return new RepoDbContext(config);
        }

        public List<DbmsLibrary> GetDbmsList()
        {
            using (var context = CreateContext())
                return context.DbmsLibraries.OrderBy(d => d.DisplayName).ToList();
        }

        public List<DbmsVersion> GetDbmsVersions(int dbmsId)
        {
            using (var context = CreateContext())
                return context.DbmsVersions
                    .Where(v => v.DbmsId == dbmsId)
                    .OrderBy(v => v.DisplayName)
                    .ToList();
        }

        public List<ObjectType> GetObjectTypes()
        {
            if (_objectTypesCache != null) return _objectTypesCache;
            lock (_objectTypesGate)
            {
                if (_objectTypesCache != null) return _objectTypesCache;
                using (var context = CreateContext())
                    _objectTypesCache = context.ObjectTypes.OrderBy(o => o.Name).ToList();
                return _objectTypesCache;
            }
        }

        public List<PropertyDef> GetPropertyDefs(int dbmsVersionId, int objectTypeId, bool erwinMode = false)
        {
            // After the schema rename: MC_PROPERTY_DEF lost CONFIG_ID and uses
            // (DBMS_VERSION_ID, OBJECT_TYPE_ID) for scoping. erwinMode=true means
            // "DBMS-agnostic only" (DBMS_VERSION_ID IS NULL); regular mode pulls both
            // the version-specific rows AND the agnostic ones (NULL) so a UI doesn't
            // miss generic properties.
            using (var context = CreateContext())
            {
                if (erwinMode)
                {
                    return context.PropertyDefs
                        .Include(pd => pd.EnumOptions)
                        .Where(pd => pd.DbmsVersionId == null && pd.ObjectTypeId == objectTypeId)
                        .OrderBy(pd => pd.GroupName)
                        .ThenBy(pd => pd.SortOrder)
                        .ToList();
                }
                return context.PropertyDefs
                    .Include(pd => pd.EnumOptions)
                    .Where(pd => (pd.DbmsVersionId == dbmsVersionId || pd.DbmsVersionId == null)
                                 && pd.ObjectTypeId == objectTypeId)
                    .OrderBy(pd => pd.GroupName)
                    .ThenBy(pd => pd.SortOrder)
                    .ToList();
            }
        }

        public List<EnumOption> GetEnumOptions(int propertyDefId)
        {
            using (var context = CreateContext())
            {
                return context.EnumOptions
                    .Where(eo => eo.PropertyDefId == propertyDefId)
                    .OrderBy(eo => eo.SortOrder)
                    .ToList();
            }
        }

        public List<ModelStandard> GetModelStandards(int modelId)
        {
            // 'modelId' is the active CONFIG.ID after the rename — the parameter
            // name is preserved on the interface for source-compat with callers
            // that haven't migrated yet.
            using (var context = CreateContext())
            {
                return context.ModelStandards
                    .Include(ps => ps.PropertyDef)
                    .Where(ps => ps.ConfigId == modelId)
                    .ToList();
            }
        }

        // Question-based property assignment (read-only).
        // NOTE: 'platformId' is now interpreted as DBMS_VERSION_ID (the schema renamed
        // MC_PLATFORM -> DBMS_LIBRARY/DBMS_VERSION; question rows are scoped on
        // DBMS_VERSION_ID). Parameter name kept for caller compatibility — rename
        // separately along with the caller in PropertyApplicatorService.
        public List<QuestionDef> GetQuestions(int platformId, int objectTypeId)
        {
            using (var context = CreateContext())
            {
                var ctx = ConfigContextService.Instance;
                int cfgId = ctx.IsInitialized ? ctx.ActiveConfigId : -1;

                return context.QuestionDefs
                    .Include(q => q.QuestionOptions)
                    .Include(q => q.QuestionRules)
                        .ThenInclude(r => r.PropertyDef)
                    .Where(q => q.DbmsVersionId == platformId
                                && q.ObjectTypeId == objectTypeId
                                && q.ConfigId == cfgId)
                    .OrderBy(q => q.SortOrder)
                    .ToList();
            }
        }

        public List<QuestionOption> GetQuestionOptions(int questionDefId)
        {
            using (var context = CreateContext())
            {
                return context.QuestionOptions
                    .Where(qo => qo.QuestionDefId == questionDefId)
                    .OrderBy(qo => qo.SortOrder)
                    .ToList();
            }
        }

        public List<QuestionRule> GetRulesForAnswer(int questionDefId, string answerValue)
        {
            using (var context = CreateContext())
            {
                return context.QuestionRules
                    .Include(r => r.PropertyDef)
                    .Where(r => r.QuestionDefId == questionDefId && r.AnswerValue == answerValue)
                    .ToList();
            }
        }

        // Write operations — not used by add-in, throw NotSupportedException
        public PropertyDef SavePropertyDef(PropertyDef def) => throw new NotSupportedException("Add-in is read-only");
        public void DeletePropertyDef(int id) => throw new NotSupportedException("Add-in is read-only");
        public void SaveEnumOptions(int propertyDefId, List<EnumOption> options) => throw new NotSupportedException("Add-in is read-only");
        public void SaveModelStandard(int modelId, int propertyDefId, string value) => throw new NotSupportedException("Add-in is read-only");
        public void DeleteModelStandard(int modelId, int propertyDefId) => throw new NotSupportedException("Add-in is read-only");
        public QuestionDef SaveQuestionDef(QuestionDef def) => throw new NotSupportedException("Add-in is read-only");
        public void DeleteQuestionDef(int id) => throw new NotSupportedException("Add-in is read-only");
        public void SaveQuestionOptions(int questionDefId, List<QuestionOption> options) => throw new NotSupportedException("Add-in is read-only");
        public void SaveQuestionRule(QuestionRule rule) => throw new NotSupportedException("Add-in is read-only");
        public void DeleteQuestionRule(int id) => throw new NotSupportedException("Add-in is read-only");
    }
}
