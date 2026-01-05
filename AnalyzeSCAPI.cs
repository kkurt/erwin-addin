using System;
using System.Reflection;

class Program
{
    static void Main()
    {
        try
        {
            var assembly = Assembly.LoadFrom(@"C:\Users\Administrator\source\repos\erwin-addin\Interop.SCAPI.dll");
            Console.WriteLine("Loaded: " + assembly.FullName);
            Console.WriteLine();

            foreach (var type in assembly.GetTypes())
            {
                Console.WriteLine("=== " + type.FullName + " ===");

                // Properties
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    Console.WriteLine("  Property: " + prop.PropertyType.Name + " " + prop.Name);
                }

                // Methods
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    var parameters = string.Join(", ", Array.ConvertAll(method.GetParameters(), p => p.ParameterType.Name + " " + p.Name));
                    Console.WriteLine("  Method: " + method.ReturnType.Name + " " + method.Name + "(" + parameters + ")");
                }

                Console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex.Message);
        }
    }
}
