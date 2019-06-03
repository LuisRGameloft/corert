// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable
using System.IO;
using System.Reflection;

using LibraryNameVariation = System.Runtime.Loader.LibraryNameVariation;

namespace System.Runtime.InteropServices
{
    public static partial class NativeLibrary
    {
        internal static IntPtr LoadLibraryByName(string libraryName, Assembly assembly, DllImportSearchPath? searchPath, bool throwOnError)
        {
            // First checks if a default dllImportSearchPathFlags was passed in, if so, use that value.
            // Otherwise checks if the assembly has the DefaultDllImportSearchPathsAttribute attribute. 
            // If so, use that value.

            int searchPathFlags;
            bool searchAssemblyDirectory;
            if (searchPath.HasValue)
            {
                searchPathFlags = (int)(searchPath.Value & ~DllImportSearchPath.AssemblyDirectory);
                searchAssemblyDirectory = (searchPath.Value & DllImportSearchPath.AssemblyDirectory) != 0;
            }
            else
            {
                GetDllImportSearchPathFlags(assembly, out searchPathFlags, out searchAssemblyDirectory);
            }

            LoadLibErrorTracker errorTracker = default;
            IntPtr ret = LoadLibraryModuleBySearch(assembly, searchAssemblyDirectory, searchPathFlags, ref errorTracker, libraryName);
            if (throwOnError && ret == IntPtr.Zero)
            {
                errorTracker.Throw(libraryName);
            }

            return ret;
        }

        // TODO: make this into a reflection callback so that we can make this work when reflection is disabled.
        private static void GetDllImportSearchPathFlags(Assembly callingAssembly, out int searchPathFlags, out bool searchAssemblyDirectory)
        {
            searchAssemblyDirectory = true;
            searchPathFlags = 0;

            foreach (CustomAttributeData cad in callingAssembly.CustomAttributes)
            {
                if (cad.AttributeType == typeof(DefaultDllImportSearchPathsAttribute))
                {
                    var attributeValue = (DllImportSearchPath)cad.ConstructorArguments[0].Value;
                    searchPathFlags = (int)(attributeValue & ~DllImportSearchPath.AssemblyDirectory);
                    searchAssemblyDirectory = (attributeValue & DllImportSearchPath.AssemblyDirectory) != 0;
                }
            }
        }

        private static IntPtr LoadLibraryModuleBySearch(Assembly callingAssembly, bool searchAssemblyDirectory, int dllImportSearchPathFlags, ref LoadLibErrorTracker errorTracker, string libraryName)
        {
            IntPtr ret = IntPtr.Zero;

            int loadWithAlteredPathFlags = 0;
            bool libNameIsRelativePath = !Path.IsPathFullyQualified(libraryName);

            // P/Invokes are often declared with variations on the actual library name.
            // For example, it's common to leave off the extension/suffix of the library
            // even if it has one, or to leave off a prefix like "lib" even if it has one
            // (both of these are typically done to smooth over cross-platform differences). 
            // We try to dlopen with such variations on the original.
            foreach (LibraryNameVariation libraryNameVariation in LibraryNameVariation.DetermineLibraryNameVariations(libraryName, libNameIsRelativePath))
            {
                string currLibNameVariation = libraryNameVariation.Prefix + libraryName + libraryNameVariation.Suffix;
                
                if (!libNameIsRelativePath)
                {
                    int flags = loadWithAlteredPathFlags;
                    if ((dllImportSearchPathFlags & (int)DllImportSearchPath.UseDllDirectoryForDependencies) != 0)
                    {
                        // LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR is the only flag affecting absolute path. Don't OR the flags
                        // unconditionally as all absolute path P/Invokes could then lose LOAD_WITH_ALTERED_SEARCH_PATH.
                        flags |= dllImportSearchPathFlags;
                    }

                    ret = LoadLibraryHelper(currLibNameVariation, flags, ref errorTracker);
                    if (ret != IntPtr.Zero)
                    {
                        return ret;
                    }
                }
                else if ((callingAssembly != null) && searchAssemblyDirectory)
                {
                    // Try to load the module alongside the assembly where the PInvoke was declared.
                    // This only makes sense in dynamic scenarios (JIT/interpreter), so leaving this out for now.
                }

                ret = LoadLibraryHelper(currLibNameVariation, dllImportSearchPathFlags, ref errorTracker);
                if (ret != IntPtr.Zero)
                {
                    return ret;
                }
            }

            return IntPtr.Zero;
        }

        private static IntPtr LoadFromPath(string libraryName, bool throwOnError)
        {
            LoadLibErrorTracker errorTracker = default;
            IntPtr ret = LoadLibraryHelper(libraryName, 0, ref errorTracker);
            if (throwOnError && ret == IntPtr.Zero)
            {
                errorTracker.Throw(libraryName);
            }

            return ret;
        }

        private static IntPtr LoadLibraryHelper(string libraryName, int flags, ref LoadLibErrorTracker errorTracker)
        {
#if PLATFORM_WINDOWS
            IntPtr ret = Interop.mincore.LoadLibraryEx(libraryName, IntPtr.Zero, flags);
            if (ret != IntPtr.Zero)
            {
                return ret;
            }

            int lastError = Marshal.GetLastWin32Error();
            if (lastError != LoadLibErrorTracker.ERROR_INVALID_PARAMETER)
            {
                errorTracker.TrackErrorCode(lastError);
            }

            return ret;
#else
            IntPtr ret = IntPtr.Zero;
            if (libraryName == null)
            {
                errorTracker.TrackErrorCode(LoadLibErrorTracker.ERROR_MOD_NOT_FOUND);
            }
            else if (libraryName == String.Empty)
            {
                errorTracker.TrackErrorCode(LoadLibErrorTracker.ERROR_INVALID_PARAMETER);
            }
            else
            {
                // TODO: FileDosToUnixPathA
                ret = Interop.Sys.LoadLibrary(libraryName);
                if (ret == IntPtr.Zero)
                {
                    errorTracker.TrackErrorCode(LoadLibErrorTracker.ERROR_MOD_NOT_FOUND);
                }
            }

            return ret;
#endif
        }

        private static void FreeLib(IntPtr handle)
        {
            // FreeLibrary doesn't throw if the input is null.
            // This avoids further null propagation/check while freeing resources (ex: in finally blocks)
            if (handle == IntPtr.Zero)
                return;

#if !PLATFORM_UNIX
            bool result = Interop.mincore.FreeLibrary(handle);
            if (!result)
                throw new InvalidOperationException();
#else
            Interop.Sys.FreeLibrary(handle);
#endif
        }

        private static IntPtr GetSymbol(IntPtr handle, string symbolName, bool throwOnError)
        {
#if !PLATFORM_UNIX
            IntPtr ret = Interop.mincore.GetProcAddress(handle, symbolName);
#else
            IntPtr ret = Interop.Sys.GetProcAddress(handle, symbolName);
#endif
            if (throwOnError && ret == IntPtr.Zero)
                throw new EntryPointNotFoundException(SR.Format(SR.Arg_EntryPointNotFoundExceptionParameterizedNoLibrary, symbolName));

            return ret;
        }

        // TODO: copy the nice error logic from CoreCLR's LoadLibErrorTracker
        // to get fine-grained error messages that take into account access denied, etc.
        internal struct LoadLibErrorTracker
        {
            internal const int ERROR_INVALID_PARAMETER = 0x57;
            internal const int ERROR_MOD_NOT_FOUND = 126;
            internal const int ERROR_BAD_EXE_FORMAT = 193;

            private int _errorCode;

            public void Throw(string libraryName)
            {
                if (_errorCode == ERROR_BAD_EXE_FORMAT)
                {
                    throw new BadImageFormatException();
                }

                throw new DllNotFoundException(SR.Format(SR.Arg_DllNotFoundExceptionParameterized, libraryName));
            }

            public void TrackErrorCode(int errorCode)
            {
                _errorCode = errorCode;
            }
        }
    }
}
