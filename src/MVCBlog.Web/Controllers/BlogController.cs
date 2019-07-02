﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Markdig;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.SyndicationFeed;
using Microsoft.SyndicationFeed.Rss;
using MVCBlog.Business;
using MVCBlog.Business.Commands;
using MVCBlog.Business.IO;
using MVCBlog.Data;
using MVCBlog.Web.Infrastructure.Paging;
using MVCBlog.Web.Models.Blog;

namespace MVCBlog.Web.Controllers
{
    public class BlogController : Controller
    {
        private const int EntriesPerPage = 10;

        private readonly EFUnitOfWork unitOfWork;

        private readonly IBlogEntryFileFileProvider fileProvider;

        private readonly ICommandHandler<AddBlogEntryCommentCommand> addBlogEntryCommentCommandHandler;

        private readonly ICommandHandler<DeleteBlogEntryCommentCommand> deleteBlogEntryCommentCommandHander;

        private readonly ICommandHandler<DeleteBlogEntryCommand> deleteBlogEntryCommandHander;

        private readonly ICommandHandler<IncrementBlogEntryVisitsCommand> incrementBlogEntryVisitsCommand;

        private readonly ICommandHandler<IncrementBlogEntryFileCounterCommand> incrementBlogEntryFileCounterCommandHandler;

        private readonly BlogSettings blogSettings;

        public BlogController(
            EFUnitOfWork unitOfWork,
            IBlogEntryFileFileProvider fileProvider,
            ICommandHandler<AddBlogEntryCommentCommand> addBlogEntryCommentCommandHandler,
            ICommandHandler<DeleteBlogEntryCommentCommand> deleteBlogEntryCommentCommandHander,
            ICommandHandler<DeleteBlogEntryCommand> deleteBlogEntryCommandHander,
            ICommandHandler<IncrementBlogEntryVisitsCommand> incrementBlogEntryVisitsCommand,
            ICommandHandler<IncrementBlogEntryFileCounterCommand> incrementBlogEntryFileCounterCommandHandler,
            IOptions<BlogSettings> optionsAccessor)
        {
            this.unitOfWork = unitOfWork;
            this.fileProvider = fileProvider;

            this.addBlogEntryCommentCommandHandler = addBlogEntryCommentCommandHandler;
            this.deleteBlogEntryCommentCommandHander = deleteBlogEntryCommentCommandHander;
            this.deleteBlogEntryCommandHander = deleteBlogEntryCommandHander;
            this.incrementBlogEntryVisitsCommand = incrementBlogEntryVisitsCommand;
            this.incrementBlogEntryFileCounterCommandHandler = incrementBlogEntryFileCounterCommandHandler;

            this.blogSettings = optionsAccessor.Value;
        }

        [Route("")]
        [Route("[controller]")]
        [Route("[controller]/Index")]
        [Route("[controller]/Tag/{tag}")]
        public async Task<ActionResult> Index(Paging<BlogEntry> paging, string tag, string search)
        {
            paging.SetSortExpression(p => p.PublishDate);
            paging.SortDirection = SortDirection.Descending;
            paging.Top = EntriesPerPage;

            var query = this.unitOfWork.BlogEntries
                            .Include(b => b.Author)
                            .Include(b => b.BlogEntryComments)
                            .Include(b => b.Tags)
                            .ThenInclude(b => b.Tag)
                            .AsNoTracking()
                            .Where(e => (e.Visible && e.PublishDate <= DateTimeOffset.Now) || this.User.Identity.IsAuthenticated);

            if (!string.IsNullOrEmpty(tag))
            {
                query = query.Where(e => e.Tags.Any(t => t.Tag.Name.Equals(tag, StringComparison.OrdinalIgnoreCase)));
            }

            if (!string.IsNullOrEmpty(search))
            {
                foreach (var item in search.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    query = query.Where(e => e.Header.Contains(item));
                }
            }

            var model = new IndexViewModel()
            {
                Entries = await query.GetPagedResultAsync(paging),
                Tags = await this.unitOfWork.Tags
                    .AsNoTracking()
                    .OrderBy(t => t.Name)
                    .ToListAsync(),
                PopularBlogEntries = await this.unitOfWork.BlogEntries
                    .AsNoTracking()
                    .Where(e => (e.Visible && e.PublishDate <= DateTimeOffset.Now) || this.User.Identity.IsAuthenticated)
                    .OrderByDescending(b => b.Visits)
                    .Take(5)
                    .ToListAsync(),
                Tag = tag,
                Search = search
            };

            return this.View(model);
        }

        /// <summary>
        /// Shows a single <see cref="BlogEntry"/>.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>A view showing a single <see cref="BlogEntry"/>.</returns>
        [Route("[controller]/{year:int}/{month:int}/{day:int}/{id}")]
        public async Task<ActionResult> Entry(string id)
        {
            var entry = await this.GetByPermalink(id);

            if (entry == null)
            {
                return this.NotFound();
            }

            if (!this.User.Identity.IsAuthenticated)
            {
                await this.incrementBlogEntryVisitsCommand.HandleAsync(new IncrementBlogEntryVisitsCommand()
                {
                    Id = entry.Id
                });

                entry.Visits++;
            }

            return this.View(new EntryViewModel()
            {
                BlogEntry = entry,
                RelatedBlogEntries = await this.GetRelatedBlogEntries(entry)
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        [Route("[controller]/{year:int}/{month:int}/{day:int}/{id}")]
        public async Task<ActionResult> Entry(string id, EntryViewModel model)
        {
            var entry = await this.GetByPermalink(id);

            if (entry == null)
            {
                return this.NotFound();
            }

            if (!this.ModelState.IsValid)
            {
                model.BlogEntry = entry;
                model.RelatedBlogEntries = await this.GetRelatedBlogEntries(entry);

                return this.View(model);
            }

            var blogEntryComment = new BlogEntryComment()
            {
                Name = model.Comment.Name,
                Email = model.Comment.Email,
                Homepage = model.Comment.Homepage,
                Comment = model.Comment.Comment,
                AdminPost = this.User.Identity.IsAuthenticated,
                BlogEntryId = entry.Id
            };

            await this.addBlogEntryCommentCommandHandler.HandleAsync(new AddBlogEntryCommentCommand()
            {
                Entity = blogEntryComment,
                Referer = this.Request.GetTypedHeaders().Referer?.ToString()
            });

            return this.RedirectToAction(nameof(this.Entry), new { Id = id });
        }

        /// <summary>
        /// Returns an RSS-Feed of all <see cref="BlogEntry">BlogEntries</see>.
        /// </summary>
        /// <returns>RSS-Feed of all <see cref="BlogEntry">BlogEntries</see>.</returns>
        [ResponseCache(Duration = 3600)]
        public async Task<ActionResult> Feed()
        {
            var entries = await this.unitOfWork.BlogEntries
                .Include(b => b.Author)
                .Include(b => b.Tags)
                .ThenInclude(t => t.Tag)
                .AsNoTracking()
                .Where(b => b.Visible && b.PublishDate <= DateTimeOffset.UtcNow)
                .OrderByDescending(b => b.PublishDate)
                .ToListAsync();

            string baseUrl = $"{this.Request.Scheme}://{this.Request.Host}{this.Request.PathBase}";

            using (var sw = new StringWriterWithEncoding(Encoding.UTF8))
            using (XmlWriter xmlWriter = XmlWriter.Create(sw, new XmlWriterSettings() { Async = true, Indent = true }))
            {
                var writer = new RssFeedWriter(xmlWriter);
                await writer.WriteTitle(this.blogSettings.BlogName);
                await writer.WriteDescription(this.blogSettings.BlogDescription);
                await writer.Write(new SyndicationLink(new Uri($"{baseUrl}/Blog/{nameof(this.Feed)}")));

                if (entries.Count > 0)
                {
                    await writer.WritePubDate(entries[0].PublishDate);
                }

                var pipeline = new MarkdownPipelineBuilder()
                    .UseAdvancedExtensions()
                    .Build();

                foreach (var blogEntry in entries)
                {
                    var syndicationItem = new SyndicationItem()
                    {
                        Id = blogEntry.Id.ToString(),
                        Title = blogEntry.Header,
                        Published = blogEntry.PublishDate,
                        LastUpdated = blogEntry.PublishDate > blogEntry.UpdateDate ? blogEntry.PublishDate : blogEntry.UpdateDate,
                        Description = $"{Markdown.ToHtml(blogEntry.ShortContent, pipeline)}{Markdown.ToHtml(blogEntry.Content, pipeline)}"
                    };

                    syndicationItem.AddLink(new SyndicationLink(new Uri($"{baseUrl}/Blog/{blogEntry.Url}")));

                    syndicationItem.AddContributor(new SyndicationPerson(blogEntry.Author.UserName, blogEntry.Author.Email));

                    foreach (var tag in blogEntry.Tags)
                    {
                        syndicationItem.AddCategory(new SyndicationCategory(tag.Tag.Name));
                    }

                    await writer.Write(syndicationItem);
                }

                xmlWriter.Flush();

                return this.Content(sw.ToString(), "application/rss+xml");
            }
        }

        /// <summary>
        /// Deletes the <see cref="BlogEntry"/> with the given id.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>A view showing all <see cref="BlogEntry">BlogEntries</see>.</returns>
        [Authorize]
        public async Task<ActionResult> Delete(Guid id)
        {
            await this.deleteBlogEntryCommandHander.HandleAsync(new DeleteBlogEntryCommand()
            {
                Id = id
            });

            return this.RedirectToAction(nameof(this.Index));
        }

        /// <summary>
        /// Deletes the <see cref="Comment"/> with the given id.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>A view showing all <see cref="BlogEntry">BlogEntries</see>.</returns>
        [Authorize]
        public async Task<ActionResult> DeleteComment(Guid id)
        {
            await this.deleteBlogEntryCommentCommandHander.HandleAsync(new DeleteBlogEntryCommentCommand()
            {
                Id = id
            });

            return this.Redirect(this.Request.Headers["Referer"]);
        }

        /// <summary>
        /// Streams the <see cref="BlogEntryFile"/> with the given id.
        /// </summary>
        /// <param name="id">The id.</param>
        /// <returns>The <see cref="BlogEntryFile"/> as Download.</returns>
        public async Task<ActionResult> Download(Guid id)
        {
            var blogEntryFile = await this.unitOfWork.BlogEntryFiles
                .AsNoTracking()
                .SingleOrDefaultAsync(b => b.Id == id);

            if (blogEntryFile == null)
            {
                return this.NotFound();
            }

            if (!this.User.Identity.IsAuthenticated)
            {
                await this.incrementBlogEntryFileCounterCommandHandler.HandleAsync(new IncrementBlogEntryFileCounterCommand()
                {
                    Id = blogEntryFile.Id
                });
            }

            var data = await this.fileProvider.GetFileAsync(blogEntryFile.Path);

            var file = new FileContentResult(data, "application/octet-stream");
            file.FileDownloadName = blogEntryFile.Name;
            return file;
        }

        /// <summary>
        /// Returns the <see cref="BlogEntry"/> with the given header.
        /// </summary>
        /// <param name="header">The header.</param>
        /// <returns>
        /// The <see cref="BlogEntry"/> with the given header.
        /// </returns>
        private async Task<BlogEntry> GetByPermalink(string header)
        {
            var entry = await this.unitOfWork.BlogEntries
                .Include(b => b.Author)
                .Include(b => b.Tags)
                .ThenInclude(b => b.Tag)
                .Include(b => b.BlogEntryComments)
                .Include(b => b.BlogEntryFiles)
                .AsNoTracking()
                .Where(e => (e.Visible && e.PublishDate <= DateTimeOffset.UtcNow) || this.User.Identity.IsAuthenticated)
                .SingleOrDefaultAsync(e => e.Permalink.Equals(header));

            return entry;
        }

        /// <summary>
        /// Returns related <see cref="BlogEntry">BlogEntries</see>.
        /// </summary>
        /// <param name="entry">The <see cref="BlogEntry"/>.</param>
        /// <returns>
        /// The related <see cref="BlogEntry">BlogEntries</see>.
        /// </returns>
        private async Task<List<BlogEntry>> GetRelatedBlogEntries(BlogEntry entry)
        {
            var tagIds = entry.Tags.Select(t => t.TagId).ToList();

            var query = await this.unitOfWork.BlogEntries
                .AsNoTracking()
                .Where(e => e.Visible && e.PublishDate <= DateTimeOffset.UtcNow && e.Id != entry.Id)
                .Where(e => e.Tags.Any(t => tagIds.Contains(t.TagId)))
                .OrderByDescending(e => e.Tags.Count(t => tagIds.Contains(t.TagId)))
                .ThenByDescending(e => e.CreatedOn)
                .Take(3)
                .ToListAsync();

            return query;
        }

        private class StringWriterWithEncoding : StringWriter
        {
            private readonly Encoding encoding;

            public StringWriterWithEncoding(Encoding encoding)
            {
                this.encoding = encoding;
            }

            public override Encoding Encoding
            {
                get { return this.encoding; }
            }
        }
    }
}