﻿using SharpGen.Config;
using SharpGen.CppModel;
using SharpGen.Logging;
using SharpGen.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SharpGen.Transform
{
    public class AssemblyManager
    {
        private List<CsAssembly> assemblies = new List<CsAssembly>();
        /// <summary>
        /// Gets assembly list that are processed.
        /// </summary>
        /// <value>The assembly list that are processed.</value>
        public IReadOnlyList<CsAssembly> Assemblies => assemblies;

        /// <summary>
        /// Gets a C# assembly by its name.
        /// </summary>
        /// <param name="assemblyName">Name of the assembly.</param>
        /// <returns>A C# assembly</returns>
        public CsAssembly GetOrCreateAssembly(string assemblyName)
        {
            var selectedAssembly = Assemblies.FirstOrDefault(assembly => assembly.Name == assemblyName);
            if (selectedAssembly == null)
            {
                selectedAssembly = new CsAssembly(assemblyName);
                assemblies.Add(selectedAssembly);
            }

            return selectedAssembly;
        }

        /// <summary>
        /// Gets the C# namespace by its name and its assembly name.
        /// </summary>
        /// <param name="assemblyName">Name of the assembly.</param>
        /// <param name="namespaceName">Name of the namespace.</param>
        /// <returns>A C# namespace</returns>
        public CsNamespace GetOrCreateNamespace(string assemblyName, string namespaceName)
        {
            if (assemblyName == null)
                assemblyName = namespaceName;

            var selectedAssembly = GetOrCreateAssembly(assemblyName);
            var selectedCsNamespace = selectedAssembly.Namespaces.FirstOrDefault(nameSpaceObject => nameSpaceObject.Name == namespaceName);
            if (selectedCsNamespace == null)
            {
                selectedCsNamespace = new CsNamespace(namespaceName);
                selectedAssembly.Add(selectedCsNamespace);
            }
            return selectedCsNamespace;
        }
    }
}
