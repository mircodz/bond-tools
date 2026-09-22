using System;
using Bond.TestSupport;

namespace Bond.Models.Tests;

internal static class GeneratedScenario
{
    public static void Run(string generated, string body, string extraSource = "")
    {
        var checks = """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            using BondTools.Models;
            public static class Scenario
            {
                private static void Require(bool condition, string message)
                {
                    if (!condition)
                    {
                        throw new Exception(message);
                    }
                }

                public static void Run()
                {
            """ + body + """
                }
            }
            """ + extraSource;

        var assembly = GeneratedCode.Compile(generated, checks);
        var run = assembly.GetType("Scenario", true)!
            .GetMethod("Run")!
            .CreateDelegate<Action>();

        run();
    }
}
