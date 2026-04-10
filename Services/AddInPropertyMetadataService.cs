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

        public List<Platform> GetPlatforms()
        {
            using (var context = CreateContext())
                return context.Platforms.OrderBy(p => p.Name).ToList();
        }

        public List<ObjectType> GetObjectTypes()
        {
            using (var context = CreateContext())
                return context.ObjectTypes.OrderBy(o => o.Name).ToList();
        }

        public List<PropertyDef> GetPropertyDefs(int platformId, int objectTypeId, bool erwinMode = false)
        {
            using (var context = CreateContext())
            {
                return context.PropertyDefs
                    .Include(pd => pd.EnumOptions)
                    .Where(pd => pd.PlatformId == platformId && pd.ObjectTypeId == objectTypeId)
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
            using (var context = CreateContext())
            {
                return context.ModelStandards
                    .Include(ps => ps.PropertyDef)
                    .Where(ps => ps.ModelId == modelId)
                    .ToList();
            }
        }

        // Question-based property assignment (read-only)
        public List<QuestionDef> GetQuestions(int platformId, int objectTypeId)
        {
            using (var context = CreateContext())
            {
                return context.QuestionDefs
                    .Include(q => q.QuestionOptions)
                    .Include(q => q.QuestionRules)
                        .ThenInclude(r => r.PropertyDef)
                    .Where(q => q.PlatformId == platformId && q.ObjectTypeId == objectTypeId)
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
