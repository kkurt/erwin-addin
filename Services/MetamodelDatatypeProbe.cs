using System;
using System.Collections.Generic;
using System.Linq;

namespace EliteSoft.Erwin.AddIn.Services
{
    /// <summary>
    /// TEMPORARY discovery service. Opens an erwin metamodel session and dumps every
    /// Property_Type / object class whose name contains "Data_Type" or "Datatype" so we
    /// can identify the source feeding the Physical Data Type dropdown.
    ///
    /// Goal: figure out where the dropdown's tree (Datatypes -> char, char varying(), ...)
    /// is stored. Once we know the metamodel class + property layout, we can constrain it
    /// per model instead of fighting the UI with mouse hooks.
    ///
    /// Runs once at startup. Output prefixed [DTProbe]. Remove after the actual filter
    /// is implemented.
    /// </summary>
    public sealed class MetamodelDatatypeProbe
    {
        private readonly dynamic _scapi;
        private readonly dynamic _currentPU;

        public event Action<string> OnLog;

        public MetamodelDatatypeProbe(dynamic scapi, dynamic currentPU)
        {
            _scapi = scapi;
            _currentPU = currentPU;
        }

        public void Run()
        {
            if (_scapi == null || _currentPU == null)
            {
                Log("[DTProbe] scapi or PU is null - skipping");
                return;
            }

            dynamic mmSession = null;
            try
            {
                mmSession = _scapi.Sessions.Add();
                // Level 1 = SCD_SL_M1 (metamodel level) — same call site UdpRuntimeService uses.
                mmSession.Open(_currentPU, 1);
                dynamic mmObjects = mmSession.ModelObjects;
                dynamic mmRoot = mmObjects.Root;

                Log("[DTProbe] Metamodel session opened");

                // (1) Walk Property_Type entries; pick out anything whose name mentions
                //     a datatype-related token. This catches Physical_Data_Type,
                //     Logical_Data_Type, Domain.*Datatype etc.
                ProbePropertyTypes(mmObjects, mmRoot);

                // (2) Walk root-level class names. The dropdown might enumerate instances
                //     of a dedicated class (e.g. "Datatype", "Domain_Datatype") rather
                //     than a list-valued Property_Type.
                ProbeClassesUnderRoot(mmObjects, mmRoot);

                // (3) Domain Parent dropdown is also a SysTreeView32 (loglardan), so
                //     dumping Domain instances at the model level (live model, not
                //     metamodel) is informative — same tree shape suggests same source.
                ProbeDomainsAtModelLevel();
            }
            catch (Exception ex)
            {
                Log($"[DTProbe] error: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { mmSession?.Close(); } catch { }
            }
        }

        private void ProbePropertyTypes(dynamic mmObjects, dynamic mmRoot)
        {
            try
            {
                dynamic propertyTypes = mmObjects.Collect(mmRoot, "Property_Type");
                if (propertyTypes == null) { Log("[DTProbe] Property_Type collection was null"); return; }

                int total = 0, hits = 0;
                foreach (dynamic pt in propertyTypes)
                {
                    if (pt == null) continue;
                    total++;
                    string name = "";
                    try { name = pt.Name?.ToString() ?? ""; } catch { }
                    if (string.IsNullOrEmpty(name)) continue;
                    if (name.IndexOf("data_type", StringComparison.OrdinalIgnoreCase) < 0
                        && name.IndexOf("datatype", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    hits++;
                    DumpPropertyType(pt, name);
                }
                Log($"[DTProbe] Property_Type scan: {hits} datatype-related of {total} total");
            }
            catch (Exception ex) { Log($"[DTProbe] ProbePropertyTypes error: {ex.Message}"); }
        }

        private void DumpPropertyType(dynamic pt, string name)
        {
            // We don't know which tags exist on this Property_Type, so probe the common
            // ones one-by-one. Failures are expected for tags that don't apply (e.g. a
            // non-list type has no list values) and just mean "skip".
            var probes = new[]
            {
                "tag_Udp_Data_Type", "tag_Enum_Values", "tag_Enum_Values_1", "tag_Enum_Values_2",
                "tag_Enum_Values_3", "tag_Enum_Values_4", "tag_Enum_Values_5", "tag_Enum_Values_6",
                "tag_Enum_Values_7", "tag_Enum_Values_8", "tag_Enum_Values_9", "tag_Enum_Values_10",
                "tag_Bit_Field_Values", "tag_Default_Value",
                "DBMS_Brands_And_Versions", "tag_Is_Physical", "tag_Is_Logical"
            };

            string parentClass = "";
            try { parentClass = pt.Owner?.Name?.ToString() ?? ""; } catch { }

            Log($"[DTProbe]   Property_Type '{name}' owner='{parentClass}'");

            foreach (var p in probes)
            {
                try
                {
                    var v = pt.Properties(p)?.Value;
                    if (v == null) continue;
                    string s = v.ToString();
                    if (string.IsNullOrEmpty(s)) continue;
                    if (s.Length > 200) s = s.Substring(0, 200) + "...";
                    Log($"[DTProbe]     {p} = '{s}'");
                }
                catch { /* tag absent: expected */ }
            }
        }

        private void ProbeClassesUnderRoot(dynamic mmObjects, dynamic mmRoot)
        {
            try
            {
                dynamic kids = mmObjects.Collect(mmRoot);
                if (kids == null) { Log("[DTProbe] mmRoot kids null"); return; }

                var seenClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (dynamic k in kids)
                {
                    if (k == null) continue;
                    string cls = "";
                    try { cls = k.ClassName?.ToString() ?? ""; } catch { }
                    if (string.IsNullOrEmpty(cls)) continue;
                    if (!seenClasses.Add(cls)) continue;
                    if (cls.IndexOf("data", StringComparison.OrdinalIgnoreCase) < 0
                        && cls.IndexOf("type", StringComparison.OrdinalIgnoreCase) < 0
                        && cls.IndexOf("domain", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    Log($"[DTProbe]   metamodel class under root: '{cls}'");
                }
                Log($"[DTProbe] metamodel root classes seen (filtered): {seenClasses.Count}");
            }
            catch (Exception ex) { Log($"[DTProbe] ProbeClassesUnderRoot error: {ex.Message}"); }
        }

        private void ProbeDomainsAtModelLevel()
        {
            // Live model session (already open via the addin's normal flow).
            // We re-use the active session by opening another at the standard level.
            dynamic session = null;
            try
            {
                session = _scapi.Sessions.Add();
                session.Open(_currentPU, 0); // 0 = live data level
                dynamic objs = session.ModelObjects;
                dynamic root = objs.Root;

                dynamic domains = null;
                try { domains = objs.Collect(root, "Domain"); }
                catch (Exception ex) { Log($"[DTProbe] Domain collect error: {ex.Message}"); return; }
                if (domains == null) { Log("[DTProbe] Domains collection null"); return; }

                int n = 0;
                foreach (dynamic d in domains)
                {
                    if (d == null) continue;
                    n++;
                    if (n > 30) { Log("[DTProbe]   ... (truncated, more domains exist)"); break; }
                    string dn = "";
                    try { dn = d.Name?.ToString() ?? ""; } catch { }
                    string dt = "";
                    try { dt = d.Properties("Physical_Data_Type")?.Value?.ToString() ?? ""; } catch { }
                    string parent = "";
                    try { parent = d.Properties("Parent_Domain_Ref")?.Value?.ToString() ?? ""; } catch { }
                    Log($"[DTProbe]   Domain '{dn}' Physical_Data_Type='{dt}' parent='{parent}'");
                }
                Log($"[DTProbe] Domain count = {n}");
            }
            catch (Exception ex) { Log($"[DTProbe] ProbeDomainsAtModelLevel error: {ex.Message}"); }
            finally { try { session?.Close(); } catch { } }
        }

        private void Log(string msg)
        {
            try { OnLog?.Invoke(msg); } catch { }
        }
    }
}
