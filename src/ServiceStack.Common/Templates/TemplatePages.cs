using System;
using System.Collections.Concurrent;
using System.Linq;
using ServiceStack.IO;
using ServiceStack.VirtualPath;

namespace ServiceStack.Templates
{
    public interface ITemplatePages
    {
        TemplatePage ResolveLayoutPage(TemplatePage page, string layout);
        TemplatePage AddPage(string virtualPath, IVirtualFile file);
        TemplatePage GetPage(string virtualPath);
        TemplatePage OneTimePage(string contents, string ext);
        
        TemplatePage ResolveLayoutPage(TemplateCodePage page, string layout);
        TemplateCodePage GetCodePage(string virtualPath);

        DateTime? GetLastModified(TemplatePage page);
    }

    public class TemplatePages : ITemplatePages
    {
        public TemplateContext Context { get; }

        public TemplatePages(TemplateContext context) => this.Context = context;

        public static string Layout = "layout";
        
        readonly ConcurrentDictionary<string, TemplatePage> pageMap = new ConcurrentDictionary<string, TemplatePage>(); 

        public virtual TemplatePage ResolveLayoutPage(TemplatePage page, string layout)
        {
            if (page == null)
                throw new ArgumentNullException(nameof(page));
            
            if (!page.HasInit)
                throw new ArgumentException($"Page {page.File.VirtualPath} has not been initialized");

            var layoutWithoutExt = (layout ?? Context.DefaultLayoutPage).LeftPart('.');

            var dir = page.File.Directory;
            do
            {
                var layoutPath = (dir.VirtualPath ?? "").CombineWith(layoutWithoutExt);

                if (pageMap.TryGetValue(layoutPath, out TemplatePage layoutPage))
                    return layoutPage;

                foreach (var format in Context.PageFormats)
                {
                    var layoutFile = dir.GetFile($"{layoutWithoutExt}.{format.Extension}");
                    if (layoutFile != null)
                        return AddPage(layoutPath, layoutFile);
                }
                
                if (dir.IsRoot)
                    break;
                
                dir = dir.ParentDirectory;

            } while (dir != null);
            
            return null;
        }

        public virtual TemplatePage ResolveLayoutPage(TemplateCodePage page, string layout)
        {
            if (page == null)
                throw new ArgumentNullException(nameof(page));
            
            if (!page.HasInit)
                throw new ArgumentException($"Page {page.VirtualPath} has not been initialized");

            var layoutWithoutExt = (layout ?? Context.DefaultLayoutPage).LeftPart('.');

            var lastDirPos = page.VirtualPath.LastIndexOf('/');
            var dirPath = lastDirPos >= 0
                ? page.VirtualPath.Substring(0, lastDirPos)
                : null;
            var dir = !string.IsNullOrEmpty(dirPath) 
                ? Context.VirtualFiles.GetDirectory(dirPath) 
                : Context.VirtualFiles.RootDirectory;
            do
            {
                var layoutPath = (dir.VirtualPath ?? "").CombineWith(layoutWithoutExt);

                if (pageMap.TryGetValue(layoutPath, out TemplatePage layoutPage))
                    return layoutPage;

                foreach (var format in Context.PageFormats)
                {
                    var layoutFile = dir.GetFile($"{layoutWithoutExt}.{format.Extension}");
                    if (layoutFile != null)
                        return AddPage(layoutPath, layoutFile);
                }
                
                if (dir.IsRoot)
                    break;
                
                dir = dir.ParentDirectory;

            } while (dir != null);
            
            return null;
        }

        public TemplateCodePage GetCodePage(string virtualPath) => Context.GetCodePage(virtualPath);

        public virtual TemplatePage AddPage(string virtualPath, IVirtualFile file)
        {
            return pageMap[virtualPath] = new TemplatePage(Context, file);
        }

        public virtual TemplatePage TryGetPage(string path)
        {
            var santizePath = path.Replace('\\','/').TrimPrefixes("/").LastLeftPart('.');

            return pageMap.TryGetValue(santizePath, out TemplatePage page) 
                ? page 
                : null;
        }

        public virtual TemplatePage GetPage(string path)
        {
            if (string.IsNullOrEmpty(path))
                return null;
            
            var santizePath = path.Replace('\\','/').TrimPrefixes("/").LastLeftPart('.');

            var page = TryGetPage(santizePath);
            if (page != null)
                return page;

            var isIndexPage = santizePath == string.Empty || santizePath.EndsWith("/");
            foreach (var format in Context.PageFormats)
            {
                var file = !isIndexPage
                    ? Context.VirtualFiles.GetFile($"{santizePath}.{format.Extension}")
                    : Context.VirtualFiles.GetFile($"{santizePath}{Context.IndexPage}.{format.Extension}");

                if (file != null)
                    return AddPage(file.VirtualPath.WithoutExtension(), file);
            }

            return null; 
        }

        private static readonly MemoryVirtualFiles TempFiles = new MemoryVirtualFiles();
        private static readonly InMemoryVirtualDirectory TempDir = new InMemoryVirtualDirectory(TempFiles, TemplateConstants.TempFilePath);

        public virtual TemplatePage OneTimePage(string contents, string ext)
        {
            var memFile = new InMemoryVirtualFile(TempFiles, TempDir)
            {
                FilePath = Guid.NewGuid().ToString("n") + "." + ext, 
                TextContents = contents,
            };
            var page = new TemplatePage(Context, memFile);
            page.Init().Wait(); // Safe as Memory Files are non-blocking
            return page;
        }

        public DateTime? GetLastModified(TemplatePage page)
        {
            if (page == null)
                return null;

            var maxLastModified = page.File.LastModified;
            if (page.LayoutPage != null)
            {
                maxLastModified = GetMaxLastModified(page.LayoutPage.File, maxLastModified);
            }
            else
            {
                var layout = ResolveLayoutPage(page, null);
                maxLastModified = GetMaxLastModified(layout?.File, maxLastModified);
            }

            var varFragments = page.PageFragments.OfType<PageVariableFragment>();
            foreach (var fragment in varFragments)
            {
                var filter = fragment.FilterExpressions?.FirstOrDefault();
                if (filter?.NameString == "partial")
                {
                    if (fragment.InitialValue is string partialPath)
                    {
                        Context.TryGetPage(page.VirtualPath, partialPath, out TemplatePage partialPage, out _);
                        maxLastModified = GetMaxLastModified(partialPage?.File, maxLastModified);

                        if (partialPage?.HasInit == true)
                        {
                            var partialLastModified = GetLastModified(partialPage);
                            if (partialLastModified > maxLastModified)
                                maxLastModified = partialLastModified.Value;
                        }
                    }
                }
                else if (filter?.NameString == "includeFile")
                {
                    if (fragment.InitialValue is string filePath)
                    {
                        var file = TemplateProtectedFilters.ResolveFile(Context.VirtualFiles, page.VirtualPath, filePath);
                        maxLastModified = GetMaxLastModified(file, maxLastModified);
                    }
                }
            }

            return maxLastModified;
        }

        private DateTime GetMaxLastModified(IVirtualFile file, DateTime maxLastModified)
        {
            if (file == null)
                return maxLastModified;

            file.Refresh();
            return file.LastModified > maxLastModified
                ? file.LastModified
                : maxLastModified;
        }
    }
}