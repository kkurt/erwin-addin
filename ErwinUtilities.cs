using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;
using EliteSoft.Erwin.AddIn.Forms;
using EliteSoft.Erwin.AddIn.Services;

namespace EliteSoft.Erwin.AddIn
{
    /// <summary>
    /// Utility methods for erwin Data Modeler - converted from PowerDesigner VBScript
    /// </summary>
    public static class ErwinUtilities
    {
        /// <summary>
        /// Cached SCAPI accessor name for the "default value" property on
        /// Attribute (Column) objects. erwin r10.10 does NOT recognize
        /// "Physical_Default_Value" - that throws "is not valid class id
        /// or class name for object or property". The actual accessor
        /// varies between releases / installs; we probe on first use,
        /// cache the winning name, and reuse it for the rest of the
        /// session. <c>null</c> sentinel = unknown; <c>""</c> sentinel =
        /// probe ran, nothing worked (rare - means defaults are not
        /// settable from SCAPI on this build, callers gracefully skip).
        /// 2026-05-25.
        /// </summary>
        private static string _attributeDefaultAccessor;
        private static readonly object _attributeDefaultAccessorLock = new object();

        private static readonly string[] AttributeDefaultAccessorCandidates =
        {
            "Default_Value",
            "DefaultValue",
            "Default_Value_Override",
            "Initial_Value",
            "Physical_Default_Value", // historical guess, kept last for completeness
        };

        /// <summary>
        /// Resolve and cache the working SCAPI accessor for an Attribute's
        /// default value. Probes the candidate list against the given
        /// attribute; the first name whose <c>Properties(name).Value</c>
        /// read does NOT throw "not valid class id" wins. Returns the
        /// accessor name on success, empty string when no candidate
        /// worked. Idempotent - subsequent calls return the cached value
        /// without re-probing.
        /// </summary>
        public static string ResolveAttributeDefaultAccessor(dynamic attr)
        {
            if (_attributeDefaultAccessor != null) return _attributeDefaultAccessor;
            if (attr == null) return null; // wait for a live attr before committing

            lock (_attributeDefaultAccessorLock)
            {
                if (_attributeDefaultAccessor != null) return _attributeDefaultAccessor;

                foreach (var candidate in AttributeDefaultAccessorCandidates)
                {
                    try
                    {
                        var _ = attr.Properties(candidate).Value; // read attempt
                        _attributeDefaultAccessor = candidate;
                        try { AddinLogger.Log($"ResolveAttributeDefaultAccessor: using '{candidate}'"); } catch { }
                        return candidate;
                    }
                    catch
                    {
                        // Try next candidate.
                    }
                }
                _attributeDefaultAccessor = string.Empty;
                try { AddinLogger.Log("ResolveAttributeDefaultAccessor: no candidate worked - default value accessor unavailable on this build"); } catch { }
                return string.Empty;
            }
        }

        #region Environment Constants

        public static readonly string[] Environments = new[]
        {
            "1_DEV", "2_INT", "3_FIX", "4_BAU", "5_REG", "6_PREPROD", "7_PROD"
        };

        #endregion

        #region Validation Rules

        /// <summary>
        /// Validates that a value is not empty
        /// Converted from: Rule_ValCannotBeEmpty
        /// </summary>
        public static bool ValidateNotEmpty(string value, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                AddinMessageDialog.Show($"{fieldName} alanı boş geçilemez!", "Doğrulama Hatası",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }

        #endregion

        #region File Operations

        /// <summary>
        /// Reads entire file content
        /// Converted from: ReadFile
        /// </summary>
        public static string ReadFile(string filename)
        {
            try
            {
                if (File.Exists(filename))
                {
                    return File.ReadAllText(filename, Encoding.UTF8);
                }
                return "--EMPTY FILE";
            }
            catch (Exception ex)
            {
                return $"--ERROR: {ex.Message}";
            }
        }

        /// <summary>
        /// Writes content to file
        /// </summary>
        public static bool WriteFile(string filename, string content)
        {
            try
            {
                File.WriteAllText(filename, content, Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                AddinMessageDialog.Show($"Dosya yazma hatası: {ex.Message}", "Hata",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        #endregion

        #region Environment/Branch Functions

        /// <summary>
        /// Finds target branch for integration based on current branch
        /// Converted from: FindTargetBranch
        /// </summary>
        public static string FindTargetBranch(string currentBranch, Dictionary<string, string> schemaConfig)
        {
            string targetBranch = null;
            string[] branchOrder = { "2_INT", "3_FIX", "4_BAU", "5_REG", "6_PREPROD", "7_PROD" };

            int startIndex = 0;
            switch (currentBranch)
            {
                case "1_DEV": startIndex = 0; break;
                case "2_INT": startIndex = 1; break;
                case "3_FIX": startIndex = 2; break;
                case "4_BAU": startIndex = 3; break;
                case "5_REG": startIndex = 4; break;
                case "6_PREPROD": startIndex = 5; break;
                default: return null;
            }

            for (int i = startIndex; i < branchOrder.Length; i++)
            {
                string branch = branchOrder[i];
                string schemaKey = "SchCode_" + branch.Substring(2); // Remove number prefix

                if (schemaConfig.ContainsKey(schemaKey) && !string.IsNullOrWhiteSpace(schemaConfig[schemaKey]))
                {
                    targetBranch = branch;
                    break;
                }
            }

            if (string.IsNullOrEmpty(targetBranch))
            {
                AddinMessageDialog.Show("Hedef ortam bulunamadı, lütfen ortamlara göre şema isimlerini giriniz.",
                    "Uyarı", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            return targetBranch;
        }

        #endregion

        #region DDL Script Generation

        /// <summary>
        /// Generates DDL script filename with timestamp
        /// </summary>
        public static string GenerateScriptFileName(string schemaName)
        {
            return $"DB_DDL_{schemaName}_{DateTime.Now:yyyyMMddHHmmss}.sql";
        }

        /// <summary>
        /// Adds header to generated script
        /// </summary>
        public static string AddScriptHeader(string scriptContent, string version, string userName, string environment)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"--Generated by erwin AddIn Version: {version}");
            sb.AppendLine($"--Generated by User: {userName}");
            sb.AppendLine($"--Generated for Environment: {environment}");
            sb.AppendLine($"--Generated Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.Append(scriptContent);
            return sb.ToString();
        }

        #endregion

        #region erwin SCAPI Helper Methods

        /// <summary>
        /// Creates a new entity in erwin model
        /// </summary>
        public static dynamic CreateEntity(dynamic session, string entityName)
        {
            try
            {
                int transId = session.BeginNamedTransaction("CreateEntity");
                dynamic modelObjects = session.ModelObjects;
                dynamic newEntity = modelObjects.Add("Entity");

                if (newEntity != null)
                {
                    try { newEntity.Properties("Name").Value = entityName; }
                    catch { try { newEntity.Name = entityName; } catch { } }

                    session.CommitTransaction(transId);
                    return newEntity;
                }
                else
                {
                    session.RollbackTransaction(transId);
                    return null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CreateEntity Error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Creates a new attribute/column in erwin model
        /// </summary>
        public static dynamic CreateAttribute(dynamic session, dynamic parentEntity, string attributeName)
        {
            try
            {
                int transId = session.BeginNamedTransaction("CreateAttribute");
                dynamic modelObjects = session.ModelObjects;

                // Collect from parent entity and add attribute
                dynamic newAttribute = modelObjects.Collect(parentEntity).Add("Attribute");

                if (newAttribute != null)
                {
                    try { newAttribute.Properties("Name").Value = attributeName; }
                    catch { try { newAttribute.Name = attributeName; } catch { } }

                    session.CommitTransaction(transId);
                    return newAttribute;
                }
                else
                {
                    session.RollbackTransaction(transId);
                    return null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CreateAttribute Error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets all entities from model
        /// </summary>
        public static List<string> GetAllEntities(dynamic session)
        {
            var entities = new List<string>();

            try
            {
                dynamic modelObjects = session.ModelObjects;
                dynamic entityCollection = modelObjects.Collect("Entity");

                for (int i = 0; i < entityCollection.Count; i++)
                {
                    dynamic entity = entityCollection.Item(i);
                    string name = "";
                    try { name = entity.Properties("Name").Value; }
                    catch { try { name = entity.Name; } catch { } }

                    if (!string.IsNullOrEmpty(name))
                        entities.Add(name);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"GetAllEntities Error: {ex.Message}");
            }

            return entities;
        }

        /// <summary>
        /// Creates owner/user in erwin model (equivalent to CreateUser in PowerDesigner)
        /// </summary>
        public static bool SetDefaultOwner(dynamic session, string schemaName)
        {
            try
            {
                int transId = session.BeginNamedTransaction("SetDefaultOwner");

                // Try to find or create owner
                dynamic modelObjects = session.ModelObjects;

                // Create Default_Owner object
                dynamic owner = modelObjects.Add("Default_Owner");
                if (owner != null)
                {
                    try { owner.Properties("Name").Value = schemaName; }
                    catch { }

                    session.CommitTransaction(transId);
                    return true;
                }

                session.RollbackTransaction(transId);
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SetDefaultOwner Error: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Model Validation

        /// <summary>
        /// Checks if model has errors or warnings (equivalent to checkModelRun)
        /// </summary>
        public static ModelCheckResult CheckModel(dynamic session)
        {
            var result = new ModelCheckResult();

            try
            {
                // erwin doesn't have built-in model checking like PowerDesigner
                // This is a placeholder for custom validation logic

                dynamic modelObjects = session.ModelObjects;

                // Check for entities without names
                dynamic entities = modelObjects.Collect("Entity");
                for (int i = 0; i < entities.Count; i++)
                {
                    dynamic entity = entities.Item(i);
                    string name = "";
                    try { name = entity.Properties("Name").Value; }
                    catch { }

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        result.Warnings.Add($"Entity at index {i} has no name");
                    }
                }

                result.Success = result.Errors.Count == 0;
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Model check error: {ex.Message}");
                result.Success = false;
            }

            return result;
        }

        #endregion
    }

    /// <summary>
    /// Result of model validation check
    /// </summary>
    public class ModelCheckResult
    {
        public bool Success { get; set; } = true;
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();

        public bool HasErrors => Errors.Count > 0;
        public bool HasWarnings => Warnings.Count > 0;

        public void ShowResultDialog()
        {
            if (HasErrors)
            {
                AddinMessageDialog.Show($"Model \"Error\"'ler bulunmaktadır. Düzeltip devam ediniz.\n\n{string.Join("\n", Errors)}",
                    "Model Kontrol Sonucu", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else if (HasWarnings)
            {
                var answer = AddinMessageDialog.Show($"Model \"Warning\"'ler bulunmaktadır. Devam etmek ister misiniz?\n\n{string.Join("\n", Warnings)}",
                    "Model Kontrol Sonucu", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                Success = (answer == DialogResult.Yes);
            }
        }
    }
}
