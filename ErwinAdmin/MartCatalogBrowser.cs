using System;
using System.Collections.Generic;

namespace ErwinMartBrowser
{
    class Program
    {
        // Bağlantı bilgileri
        private const string SERVER = "localhost";
        private const string PORT = "18170";
        private const string USERNAME = "Kursat";
        private const string PASSWORD = "Elite12345..";

        static void Main(string[] args)
        {
            Console.WriteLine("==============================================");
            Console.WriteLine("  Erwin Mart Catalog Browser (v2)");
            Console.WriteLine("==============================================");
            Console.WriteLine();

            dynamic scapi = null;

            try
            {
                // SCAPI'yi başlat
                Console.WriteLine("[1] SCAPI baslatiliyor...");
                
                Type scapiType = Type.GetTypeFromProgID("erwin.SCAPI");
                if (scapiType == null)
                {
                    scapiType = Type.GetTypeFromProgID("erwin9.SCAPI");
                }
                
                if (scapiType == null)
                {
                    Console.WriteLine("HATA: SCAPI bulunamadi!");
                    WaitAndExit();
                    return;
                }

                scapi = Activator.CreateInstance(scapiType);
                Console.WriteLine("[1] SCAPI baslatildi: OK");
                Console.WriteLine();

                // SCAPI'nin mevcut metodlarını listele
                Console.WriteLine("[2] SCAPI metodlari kontrol ediliyor...");
                ListAvailableMethods(scapi, "SCAPI");
                Console.WriteLine();

                // =====================================================
                // YÖNTEM 1: MartServer COM nesnesi
                // =====================================================
                Console.WriteLine("[3] Mart baglanti yontemleri deneniyor...");
                Console.WriteLine();

                dynamic martServer = null;
                bool connected = false;

                // MartServer COM nesnesi dene
                string[] martProgIds = {
                    "erwin.MartServer",
                    "erwin9.MartServer", 
                    "erwin.MartAPI",
                    "erwin9.MartAPI",
                    "erwin.Mart",
                    "erwin9.Mart",
                    "erwin.MartAdmin",
                    "erwin9.MartAdmin",
                    "SCAPI.Mart",
                    "SCAPI.MartServer"
                };

                foreach (var progId in martProgIds)
                {
                    try
                    {
                        Console.WriteLine($"    {progId} deneniyor...");
                        Type martType = Type.GetTypeFromProgID(progId);
                        
                        if (martType != null)
                        {
                            martServer = Activator.CreateInstance(martType);
                            Console.WriteLine($"    {progId}: BULUNDU!");
                            ListAvailableMethods(martServer, progId);
                            
                            // Bağlantı dene
                            try
                            {
                                martServer.Connect(SERVER, int.Parse(PORT), USERNAME, PASSWORD);
                                connected = true;
                                Console.WriteLine($"    {progId}.Connect(): BASARILI!");
                                break;
                            }
                            catch (Exception connEx)
                            {
                                Console.WriteLine($"    Connect hatasi: {connEx.Message}");
                            }

                            try
                            {
                                string connStr = $"SRV={SERVER};PRT={PORT};UID={USERNAME};PSW={PASSWORD}";
                                martServer.Open(connStr);
                                connected = true;
                                Console.WriteLine($"    {progId}.Open(): BASARILI!");
                                break;
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    {progId}: {ex.Message}");
                    }
                }

                Console.WriteLine();

                // =====================================================
                // YÖNTEM 2: PersistenceUnits ile Mart URL
                // =====================================================
                if (!connected)
                {
                    Console.WriteLine("[4] PersistenceUnits ile Mart URL deneniyor...");
                    
                    try
                    {
                        // Mart root URL - katalog listesi için
                        string[] urlFormats = {
                            $"mart://{SERVER}:{PORT}",
                            $"mart://Mart?SRV={SERVER};PRT={PORT};UID={USERNAME};PSW={PASSWORD}",
                            $"mart://{SERVER}:{PORT}/?UID={USERNAME};PSW={PASSWORD}",
                            $"mart://Mart/{SERVER}:{PORT}",
                        };

                        foreach (var url in urlFormats)
                        {
                            Console.WriteLine($"    URL: {url.Replace(PASSWORD, "******")}");
                            
                            try
                            {
                                // PersistenceUnits'e erişim
                                dynamic pu = scapi.PersistenceUnits;
                                Console.WriteLine($"    PersistenceUnits: OK (Count: {pu.Count})");

                                // Mevcut açık modelleri listele
                                if (pu.Count > 0)
                                {
                                    Console.WriteLine("    Acik modeller:");
                                    for (int i = 0; i < pu.Count; i++)
                                    {
                                        try
                                        {
                                            dynamic model = pu.Item(i);
                                            string name = "";
                                            try { name = model.Name; } catch { }
                                            try { if (string.IsNullOrEmpty(name)) name = model.FilePath; } catch { }
                                            Console.WriteLine($"      [{i}] {name}");
                                        }
                                        catch { }
                                    }
                                }
                            }
                            catch (Exception puEx)
                            {
                                Console.WriteLine($"    PU Hata: {puEx.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    Hata: {ex.Message}");
                    }
                }

                Console.WriteLine();

                // =====================================================
                // YÖNTEM 3: Registry'den Mart bilgilerini al
                // =====================================================
                Console.WriteLine("[5] Registry'den Mart COM nesneleri aranıyor...");
                SearchRegistryForErwinCOM();

                Console.WriteLine();
                Console.WriteLine("----------------------------------------------");
                Console.WriteLine();
                Console.WriteLine("SONUC: Erwin 15'te Mart'a programatik erisim icin");
                Console.WriteLine("farkli bir yaklasim gerekebilir:");
                Console.WriteLine();
                Console.WriteLine("1. erwin Data Modeler'i acin");
                Console.WriteLine("2. Mart'a manuel baglanin (File > Mart > Connect)");
                Console.WriteLine("3. Sonra SCAPI ile acik modele erisin");
                Console.WriteLine();
                Console.WriteLine("Ya da Mart REST API kullanilabilir (varsa).");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HATA: {ex.GetType().Name}");
                Console.WriteLine($"Mesaj: {ex.Message}");
            }

            WaitAndExit();
        }

        static void ListAvailableMethods(dynamic comObject, string name)
        {
            try
            {
                Type comType = comObject.GetType();
                Console.WriteLine($"    {name} Type: {comType.FullName}");

                // Reflection ile metodları al
                var methods = comType.GetMethods();
                var uniqueMethods = new HashSet<string>();
                
                foreach (var m in methods)
                {
                    // Standart .NET metodlarını filtrele
                    if (!m.Name.StartsWith("QueryInterface") && 
                        !m.Name.StartsWith("AddRef") && 
                        !m.Name.StartsWith("Release") &&
                        !m.Name.StartsWith("GetType") &&
                        !m.Name.StartsWith("ToString") &&
                        !m.Name.StartsWith("Equals") &&
                        !m.Name.StartsWith("GetHashCode") &&
                        !m.Name.StartsWith("get_") &&
                        !m.Name.StartsWith("set_"))
                    {
                        uniqueMethods.Add(m.Name);
                    }
                }

                if (uniqueMethods.Count > 0)
                {
                    Console.WriteLine($"    Metodlar: {string.Join(", ", uniqueMethods)}");
                }

                // Property'leri de listele
                var properties = comType.GetProperties();
                var propNames = new List<string>();
                foreach (var p in properties)
                {
                    propNames.Add(p.Name);
                }

                if (propNames.Count > 0)
                {
                    Console.WriteLine($"    Properties: {string.Join(", ", propNames)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Method listeleme hatasi: {ex.Message}");
            }
        }

        static void SearchRegistryForErwinCOM()
        {
            try
            {
                using (var classesRoot = Microsoft.Win32.Registry.ClassesRoot)
                {
                    var erwinKeys = new List<string>();
                    
                    foreach (var keyName in classesRoot.GetSubKeyNames())
                    {
                        if (keyName.ToLower().Contains("erwin") || 
                            keyName.ToLower().Contains("mart"))
                        {
                            erwinKeys.Add(keyName);
                        }
                    }

                    if (erwinKeys.Count > 0)
                    {
                        Console.WriteLine("    Bulunan erwin/mart COM nesneleri:");
                        foreach (var key in erwinKeys)
                        {
                            Console.WriteLine($"      - {key}");
                        }
                    }
                    else
                    {
                        Console.WriteLine("    Registry'de erwin COM nesnesi bulunamadi.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    Registry arama hatasi: {ex.Message}");
            }
        }

        static void WaitAndExit()
        {
            Console.WriteLine();
            Console.WriteLine("Cikmak icin bir tusa basin...");
            Console.ReadKey();
        }
    }
}
