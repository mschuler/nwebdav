﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Xml.Linq;
using NWebDav.Server.Helpers;
using NWebDav.Server.Locking;
using NWebDav.Server.Logging;
using NWebDav.Server.Props;

namespace NWebDav.Server.Stores
{
    public class DiskStore : IStore
    {
        [DebuggerDisplay("{_fileInfo.FullPath}")]
        private class StoreItem : IStoreItem
        {
            private static readonly ILogger Log = LoggerFactory.CreateLogger(typeof(StoreItem));
            private static readonly PropertyManager<StoreItem> ItemPropertyManager = new PropertyManager<StoreItem>(new DavProperty<StoreItem>[]
            {
                // RFC-2518 properties
                new DavCreationDate<StoreItem>
                {
                    Getter = item => item._fileInfo.CreationTimeUtc,
                    Setter = (item, value) => { item._fileInfo.CreationTimeUtc = value; return DavStatusCode.OK; }
                },
                new DavDisplayName<StoreItem>
                {
                    Getter = item => item._fileInfo.Name
                },
                new DavGetContentLength<StoreItem>
                {
                    Getter = item => item._fileInfo.Length
                },
                new DavGetContentType<StoreItem>
                {
                    Getter = item => item.DetermineContentType()
                },
                new DavGetEtag<StoreItem>
                {
                    // Calculating the Etag is an expensive operation,
                    // because we need to scan the entire file.
                    IsExpensive = true,
                    Getter = item => item.CalculateEtag()
                },
                new DavGetLastModified<StoreItem>
                {
                    Getter = item => item._fileInfo.LastWriteTimeUtc,
                    Setter = (item, value) => { item._fileInfo.LastWriteTimeUtc = value; return DavStatusCode.OK; }
                },
                new DavGetResourceType<StoreItem>
                {
                    Getter = item => null
                },

                // Default locking property handling via the LockingManager
                new DavLockDiscoveryDefault<StoreItem>(),
                new DavSupportedLockDefault<StoreItem>(),

                // Hopmann/Lippert collection properties
                // (although not a collection, the IsHidden property might be valuable)
                new DavIsHidden<StoreItem>
                {
                    Getter = item => (item._fileInfo.Attributes & FileAttributes.Hidden) != 0
                },

                // Win32 extensions
                new Win32CreationTime<StoreItem>
                {
                    Getter = item => item._fileInfo.CreationTimeUtc,
                    Setter = (item, value) => { item._fileInfo.CreationTimeUtc = value; return DavStatusCode.OK; }
                },
                new Win32LastAccessTime<StoreItem>
                {
                    Getter = item => item._fileInfo.LastAccessTimeUtc,
                    Setter = (item, value) => { item._fileInfo.LastAccessTimeUtc = value; return DavStatusCode.OK; }
                },
                new Win32LastModifiedTime<StoreItem>
                {
                    Getter = item => item._fileInfo.LastWriteTimeUtc,
                    Setter = (item, value) => { item._fileInfo.LastWriteTimeUtc = value; return DavStatusCode.OK; }
                },
                new Win32FileAttributes<StoreItem>
                {
                    Getter = item => item._fileInfo.Attributes,
                    Setter = (item, value) => { item._fileInfo.Attributes = value; return DavStatusCode.OK; }
                }
            });

            private readonly DiskStore _diskStore;
            private readonly FileInfo _fileInfo;

            public StoreItem(DiskStore diskStore, FileInfo fileInfo)
            {
                _diskStore = diskStore;
                _fileInfo = fileInfo;
            }

            public string Name => _fileInfo.Name;
            public Stream GetReadableStream(IPrincipal principal) => _fileInfo.OpenRead();
            public Stream GetWritableStream(IPrincipal principal) => _fileInfo.OpenWrite();
            public IPropertyManager PropertyManager => ItemPropertyManager;
            public ILockingManager LockingManager => _diskStore.LockingManager;

            public async Task<StoreItemResult> CopyAsync(IStoreCollection destination, string name, bool overwrite, IPrincipal principal)
            {
                try
                {
                    // If the destination is also a disk-store, then we can use the FileCopy API
                    // (it's probably a bit more efficient than copying in C#)
                    var diskCollection = destination as StoreCollection;
                    if (diskCollection != null)
                    {
                        var destinationPath = Path.Combine(diskCollection.FullPath, name);

                        // Check if the file already exists
                        var fileExists = File.Exists(destinationPath);
                        if (fileExists && !overwrite)
                            return new StoreItemResult(DavStatusCode.PreconditionFailed);

                        // Copy the file
                        File.Copy(_fileInfo.FullName, destinationPath, true);

                        // Return the appropriate status
                        return new StoreItemResult(fileExists ? DavStatusCode.NoContent : DavStatusCode.Created);
                    }
                    else
                    {
                        // Create the item in the destination collection
                        var result = await destination.CreateItemAsync(name, overwrite, principal).ConfigureAwait(false);

                        // Check if the item could be created
                        if (result.Item != null)
                        {
                            using (var destinationStream = result.Item.GetWritableStream(principal))
                            using (var sourceStream = GetReadableStream(principal))
                            {
                                await sourceStream.CopyToAsync(destinationStream).ConfigureAwait(false);
                            }
                        }

                        // Return result
                        return new StoreItemResult(result.Result, result.Item);
                    }
                }
                catch (IOException ioException) when (ioException.IsDiskFull())
                {
                    Log.Log(LogLevel.Error, "Out of disk space while copying data.", ioException);
                    return new StoreItemResult(DavStatusCode.InsufficientStorage);
                }
                catch (Exception exc)
                {
                    Log.Log(LogLevel.Error, "Unexpected exception while copying data.", exc);
                    return new StoreItemResult(DavStatusCode.InternalServerError);
                }
            }

            public override int GetHashCode()
            {
                return _fileInfo.FullName.GetHashCode();
            }

            public override bool Equals(object obj)
            {
                var storeItem = obj as StoreItem;
                if (storeItem == null)
                    return false;
                return storeItem._fileInfo.FullName.Equals(_fileInfo.FullName, StringComparison.CurrentCultureIgnoreCase);
            }

            private string DetermineContentType()
            {
                // TODO: Determine content type based on extension
                return "application/octet-stream";
            }

            private string CalculateEtag()
            {
                using (var stream = File.OpenRead(_fileInfo.FullName))
                {
                    var hash = SHA256.Create().ComputeHash(stream);
                    return BitConverter.ToString(hash).Replace("-", string.Empty);
                }
            }
        }

        [DebuggerDisplay("{_directoryInfo.FullPath}\\")]
        private class StoreCollection : IStoreCollection
        {
            private static readonly ILogger Log = LoggerFactory.CreateLogger(typeof(StoreCollection));
            private static readonly PropertyManager<StoreCollection> CollectionPropertyManager = new PropertyManager<StoreCollection>(new DavProperty<StoreCollection>[]
            {
                // RFC-2518 properties
                new DavCreationDate<StoreCollection>
                {
                    Getter = collection => collection._directoryInfo.CreationTimeUtc,
                    Setter = (collection, value) => { collection._directoryInfo.CreationTimeUtc = value; return DavStatusCode.OK; }
                },
                new DavDisplayName<StoreCollection>
                {
                    Getter = collection => collection._directoryInfo.Name
                },
                new DavGetLastModified<StoreCollection>
                {
                    Getter = collection => collection._directoryInfo.LastWriteTimeUtc,
                    Setter = (collection, value) => { collection._directoryInfo.LastWriteTimeUtc = value; return DavStatusCode.OK; }
                },
                new DavGetResourceType<StoreCollection>
                {
                    Getter = collection => new XElement(WebDavNamespaces.DavNs + "collection")
                },

                // Default locking property handling via the LockingManager
                new DavLockDiscoveryDefault<StoreCollection>(),
                new DavSupportedLockDefault<StoreCollection>(),

                // Hopmann/Lippert collection properties
                new DavChildCount<StoreCollection>
                {
                    Getter = collection => collection._directoryInfo.EnumerateFiles().Count() + collection._directoryInfo.EnumerateDirectories().Count()
                },
                new DavIsCollection<StoreCollection>
                {
                    Getter = collection => true
                },
                new DavIsFolder<StoreCollection>
                {
                    Getter = collection => true
                },
                new DavIsHidden<StoreCollection>
                {
                    Getter = collection => (collection._directoryInfo.Attributes & FileAttributes.Hidden) != 0
                },
                new DavHasSubs<StoreCollection>
                {
                    Getter = collection => collection._directoryInfo.EnumerateDirectories().Any()
                },
                new DavNoSubs<StoreCollection>
                {
                    Getter = collection => false
                },
                new DavObjectCount<StoreCollection>
                {
                    Getter = collection => collection._directoryInfo.EnumerateFiles().Count()
                },
                new DavVisibleCount<StoreCollection>
                {
                    Getter = collection => collection._directoryInfo.EnumerateFiles().Count(fi => (fi.Attributes & FileAttributes.Hidden) == 0)
                },
                
                // Win32 extensions
                new Win32CreationTime<StoreCollection>
                {
                    Getter = collection => collection._directoryInfo.CreationTimeUtc,
                    Setter = (collection, value) => { collection._directoryInfo.CreationTimeUtc = value; return DavStatusCode.OK; }
                },
                new Win32LastAccessTime<StoreCollection>
                {
                    Getter = collection => collection._directoryInfo.LastAccessTimeUtc,
                    Setter = (collection, value) => { collection._directoryInfo.LastAccessTimeUtc = value; return DavStatusCode.OK; }
                },
                new Win32LastModifiedTime<StoreCollection>
                {
                    Getter = collection => collection._directoryInfo.LastWriteTimeUtc,
                    Setter = (collection, value) => { collection._directoryInfo.LastWriteTimeUtc = value; return DavStatusCode.OK; }
                },
                new Win32FileAttributes<StoreCollection>
                {
                    Getter = collection => collection._directoryInfo.Attributes,
                    Setter = (collection, value) => { collection._directoryInfo.Attributes = value; return DavStatusCode.OK; }
                }
            });

            private readonly DiskStore _diskStore;
            private readonly DirectoryInfo _directoryInfo;

            public StoreCollection(DiskStore diskStore, DirectoryInfo directoryInfo)
            {
                _diskStore = diskStore;
                _directoryInfo = directoryInfo;
            }

            public string Name => _directoryInfo.Name;
            public string FullPath => _directoryInfo.FullName;

            // Disk collections (a.k.a. directories don't have their own data)
            public Stream GetReadableStream(IPrincipal principal) => null;
            public Stream GetWritableStream(IPrincipal principal) => null;

            public IPropertyManager PropertyManager => CollectionPropertyManager;
            public ILockingManager LockingManager => _diskStore.LockingManager;

            public Task<IStoreItem> GetItemAsync(string name, IPrincipal principal)
            {
                // Determine the full path
                var fullPath = Path.Combine(_directoryInfo.FullName, name);

                // Check if the item is a file
                if (File.Exists(fullPath))
                    return Task.FromResult<IStoreItem>(new StoreItem(_diskStore, new FileInfo(fullPath)));

                // Check if the item is a directory
                if (Directory.Exists(fullPath))
                    return Task.FromResult<IStoreItem>(new StoreCollection(_diskStore, new DirectoryInfo(fullPath)));

                // Item not found
                return Task.FromResult<IStoreItem>(null);
            }

            public Task<IList<IStoreItem>> GetItemsAsync(IPrincipal principal)
            {
                var items = new List<IStoreItem>();

                // Add all directories
                foreach (var subDirectory in _directoryInfo.GetDirectories())
                    items.Add(new StoreCollection(_diskStore, subDirectory));

                // Add all files
                foreach (var file in _directoryInfo.GetFiles())
                    items.Add(new StoreItem(_diskStore, file));

                // Return the items
                return Task.FromResult<IList<IStoreItem>>(items);
            }

            public Task<StoreItemResult> CreateItemAsync(string name, bool overwrite, IPrincipal principal)
            {
                // Determine the destination path
                var destinationPath = Path.Combine(FullPath, name);

                // Determine result
                DavStatusCode result;

                // Check if the file can be overwritten
                if (File.Exists(name))
                {
                    if (!overwrite)
                        return Task.FromResult(new StoreItemResult(DavStatusCode.PreconditionFailed));

                    result = DavStatusCode.NoContent;
                }
                else
                {
                    result = DavStatusCode.Created;
                }

                try
                {
                    // Create a new file
                    File.Create(destinationPath).Dispose();
                }
                catch (Exception exc)
                {
                    // Log exception
                    Log.Log(LogLevel.Error, $"Unable to create '{destinationPath}' file.", exc);
                    return Task.FromResult(new StoreItemResult(DavStatusCode.InternalServerError));
                }

                // Return result
                return Task.FromResult(new StoreItemResult(result, new StoreItem(_diskStore, new FileInfo(destinationPath))));
            }

            public Task<StoreCollectionResult> CreateCollectionAsync(string name, bool overwrite, IPrincipal principal)
            {
                // Determine the destination path
                var destinationPath = Path.Combine(FullPath, name);

                // Check if the directory can be overwritten
                DavStatusCode result;
                if (Directory.Exists(destinationPath))
                {
                    // Check if overwrite is allowed
                    if (!overwrite)
                        return Task.FromResult(new StoreCollectionResult(DavStatusCode.PreconditionFailed));

                    // Overwrite existing
                    result = DavStatusCode.NoContent;
                }
                else
                {
                    // Created new directory
                    result = DavStatusCode.Created;
                }

                try
                {
                    // Attempt to create the directory
                    Directory.CreateDirectory(destinationPath);
                }
                catch (Exception exc)
                {
                    // Log exception
                    Log.Log(LogLevel.Error, $"Unable to create '{destinationPath}' directory.", exc);
                    return null;
                }

                // Return the collection
                return Task.FromResult(new StoreCollectionResult(result, new StoreCollection(_diskStore, new DirectoryInfo(destinationPath))));
            }

            public async Task<StoreItemResult> CopyAsync(IStoreCollection destinationCollection, string name, bool overwrite, IPrincipal principal)
            {
                // Just create the folder itself
                var result = await destinationCollection.CreateCollectionAsync(name, overwrite, principal);
                return new StoreItemResult(result.Result, result.Collection);
            }

            public async Task<StoreItemResult> MoveItemAsync(string sourceName, IStoreCollection destinationCollection, string destinationName, bool overwrite, IPrincipal principal)
            {
                // Determine the object that is being moved
                var item = await GetItemAsync(sourceName, principal);
                if (item == null)
                    return new StoreItemResult(DavStatusCode.NotFound);

                // Check if the item is actually a file
                var diskStoreItem = item as StoreItem;
                if (diskStoreItem != null)
                {
                    // If the destination collection is a directory too, then we can simply move the file
                    var destinationDiskStoreCollection = destinationCollection as StoreCollection;
                    if (destinationDiskStoreCollection != null)
                    {
                        // Determine source and destination paths
                        var sourcePath = Path.Combine(_directoryInfo.FullName, sourceName);
                        var destinationPath = Path.Combine(destinationDiskStoreCollection._directoryInfo.FullName, destinationName);

                        // Check if the file already exists
                        DavStatusCode result;
                        if (File.Exists(destinationPath))
                        {
                            // Remove the file if it already exists (if allowed)
                            if (!overwrite)
                                return new StoreItemResult(DavStatusCode.Forbidden);

                            // The file will be overwritten
                            File.Delete(destinationPath);
                            result = DavStatusCode.NoContent;
                        }
                        else
                        {
                            // The file will be "created"
                            result = DavStatusCode.Created;
                        }

                        // Move the file
                        File.Move(sourcePath, destinationPath);
                        return new StoreItemResult(result, new StoreItem(_diskStore, new FileInfo(destinationPath)));
                    }
                    else
                    {
                        // Attempt to copy the item to the destination collection
                        var result = await item.CopyAsync(destinationCollection, destinationName, overwrite, principal);
                        if (result.Result == DavStatusCode.Created || result.Result == DavStatusCode.NoContent)
                            await DeleteItemAsync(sourceName, principal);

                        // Return the result
                        return result;
                    }
                }
                else
                {
                    // If it's not a plain item, then it's a collection
                    Debug.Assert(item is StoreCollection);

                    // Collections will never be moved, but always be created
                    // (we always move the individual items to make sure locking is checked properly)
                    throw new InvalidOperationException("Collections should never be moved directly.");
                }
            }

            public Task<DavStatusCode> DeleteItemAsync(string name, IPrincipal principal)
            {
                // Determine the full path
                var fullPath = Path.Combine(_directoryInfo.FullName, name);
                try
                {
                    // Check if the file exists
                    if (File.Exists(fullPath))
                    {
                        // Delete the file
                        File.Delete(fullPath);
                        return Task.FromResult(DavStatusCode.OK);
                    }

                    // Check if the directory exists
                    if (Directory.Exists(fullPath))
                    {
                        // Delete the directory
                        Directory.Delete(fullPath);
                        return Task.FromResult(DavStatusCode.OK);
                    }

                    // Item not found
                    return Task.FromResult(DavStatusCode.NotFound);
                }
                catch (Exception exc)
                {
                    // Log exception
                    Log.Log(LogLevel.Error, $"Unable to delete '{fullPath}' directory.", exc);
                    return Task.FromResult(DavStatusCode.InternalServerError);
                }
            }

            public bool AllowInfiniteDepthProperties => false;

            public override int GetHashCode()
            {
                return _directoryInfo.FullName.GetHashCode();
            }

            public override bool Equals(object obj)
            {
                var storeCollection = obj as StoreCollection;
                if (storeCollection == null)
                    return false;
                return storeCollection._directoryInfo.FullName.Equals(_directoryInfo.FullName, StringComparison.CurrentCultureIgnoreCase);
            }
        }

        public DiskStore(string directory, ILockingManager lockingManager = null)
        {
            BaseDirectory = directory;
            LockingManager = lockingManager ?? new InMemoryLockingManager();
        }

        private string BaseDirectory { get; }
        private ILockingManager LockingManager { get; }

        public Task<IStoreItem> GetItemAsync(Uri uri, IPrincipal principal)
        {
            // Determine the path from the uri
            var path = GetPathFromUri(uri);

            // Check if it's a directory
            if (Directory.Exists(path))
                return Task.FromResult<IStoreItem>(new StoreCollection(this, new DirectoryInfo(path)));

            // Check if it's a file
            if (File.Exists(path))
                return Task.FromResult<IStoreItem>(new StoreItem(this, new FileInfo(path)));

            // The item doesn't exist
            return Task.FromResult<IStoreItem>(null);
        }

        public Task<IStoreCollection> GetCollectionAsync(Uri uri, IPrincipal principal)
        {
            // Determine the path from the uri
            var path = GetPathFromUri(uri);
            if (!Directory.Exists(path))
                return Task.FromResult<IStoreCollection>(null);

            // Return the item
            return Task.FromResult<IStoreCollection>(new StoreCollection(this, new DirectoryInfo(path)));
        }

        private string GetPathFromUri(Uri uri)
        {
            // Determine the path
            var requestedPath = uri.AbsolutePath.Substring(1).Replace('/', '\\');

            // Determine the full path
            var fullPath = Path.GetFullPath(Path.Combine(BaseDirectory, requestedPath));

            // Make sure we're still inside the specified directory
            if (fullPath != BaseDirectory && !fullPath.StartsWith(BaseDirectory + "\\"))
                throw new SecurityException($"Uri '{uri}' is outside the '{BaseDirectory}' directory.");

            // Return the combined path
            return fullPath;
        }
    }
}
