﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.TeamFoundation.VersionControl.Client;

namespace Sep.Git.Tfs.Core
{
    class TfsChangeset : ITfsChangeset
    {
        private readonly TfsHelper tfs;
        private readonly Changeset changeset;
        public TfsChangesetInfo Summary { get; set; }

        public TfsChangeset(TfsHelper tfs, Changeset changeset)
        {
            this.tfs = tfs;
            this.changeset = changeset;
        }

        public LogEntry Apply(string lastCommit, GitIndexInfo index)
        {
            foreach(var change in Sort(changeset.Changes))
            {
                // If you make updates to a dir in TF, the changeset includes changes for all the children also,
                // and git doesn't really care if you add or delete empty dirs.
                if (change.Item.ItemType == ItemType.File)
                {
                    var pathInGitRepo = Summary.Remote.GetPathInGitRepo(change.Item.ServerItem);
                    if(pathInGitRepo == null || Summary.Remote.ShouldSkip(pathInGitRepo))
                        continue;
                    if(change.ChangeType.IncludesOneOf(ChangeType.Rename))
                    {
                        var oldPath = Summary.Remote.GetPathInGitRepo(GetPathBeforeRename(change.Item));
                        if (oldPath != null)
                        {
                            index.Remove(oldPath);
                        }
                        if (change.Item.DeletionId == 0 && !change.ChangeType.IncludesOneOf(ChangeType.Delete))
                        {
                            Update(change, pathInGitRepo, lastCommit, index);
                        }
                    }
                    else if (change.ChangeType.IncludesOneOf(ChangeType.Delete))
                    {
                        Delete(pathInGitRepo, lastCommit, index);
                    }
                    else
                    {
                        if (change.Item.DeletionId == 0)
                        {
                            Update(change, pathInGitRepo, lastCommit, index);
                        }
                    }
                }
            }
            return MakeNewLogEntry();
        }

        private IEnumerable<Change> Sort(IEnumerable<Change> changes)
        {
            return changes.OrderBy(change => Rank(change.ChangeType));
        }

        private int Rank(ChangeType type)
        {
            if (type.IncludesOneOf(ChangeType.Delete))
                return 0;
            if (type.IncludesOneOf(ChangeType.Rename))
                return 1;
            return 2;
        }

        private string GetPathBeforeRename(Item item)
        {
            return item.VersionControlServer.GetItem(item.ItemId, item.ChangesetId - 1).ServerItem;
        }

        private void Update(Change change, string pathInGitRepo, string lastCommit, GitIndexInfo index)
        {
            if (change.Item.ItemType == ItemType.File)
            {
                string mode = null;

                // It's VERY convenient that TFS renames every file in the tree, not just the dir.
                // If it didn't, then doing directory renames would be much more involved.
                // Instead, we can just handle each file's change.
                if (change.ChangeType.IncludesOneOf(ChangeType.Rename))
                {
                    var oldPath = Summary.Remote.GetPathInGitRepo(GetPathBeforeRename(change.Item));
                    if (oldPath != null)
                    {
                        mode = GetCurrentMode(lastCommit, oldPath);
                        index.Remove(oldPath);
                    }
                }
                else
                {
                    mode = GetCurrentMode(lastCommit, pathInGitRepo);
                }

                if (mode == null || change.ChangeType.IncludesOneOf(ChangeType.Add))
                    mode = "100644";
                index.Update(mode, pathInGitRepo, change.Item.DownloadFile());
            }
        }

        private void Delete(string pathInGitRepo, string lastCommit, GitIndexInfo index)
        {
            var gitObject = Summary.Remote.Repository.GetObjectInfo(lastCommit, pathInGitRepo);
            if(gitObject != null)
            {
                Summary.Remote.Repository.CommandOutputPipe(stdout =>
                                                        {
                                                            var reader = new DelimitedReader(stdout);
                                                            string fileInDir;
                                                            while((fileInDir = reader.Read()) != null)
                                                            {
                                                                var pathToRemove = pathInGitRepo + "/" +
                                                                                   fileInDir;
                                                                index.Remove(pathToRemove);
                                                                Trace.WriteLine("\tD\t" + pathToRemove);
                                                            }
                                                        }, "ls-tree", "-r", "--name-only", "-z", gitObject.Sha);
            }
            else
            {
                index.Remove(pathInGitRepo);
            }
            Trace.WriteLine("\tD\t" + pathInGitRepo);
        }

        private string GetCurrentMode(string lastChangeset, string item)
        {
            if(String.IsNullOrEmpty(lastChangeset)) return null;
            var treeInfo = Summary.Remote.Repository.Command("ls-tree", "-z", lastChangeset, "./" + item);
            var treeRegex =
                new Regex("\\A(?<mode>\\d{6}) blob (?<blob>" + GitTfsConstants.Sha1 + ")\\t" + Regex.Escape(item) + "\0");
            var match = treeRegex.Match(treeInfo);
            return !match.Success ? null : match.Groups["mode"].Value;
        }

        private LogEntry MakeNewLogEntry()
        {
            var log = new LogEntry();
            log.CommitterName = log.AuthorName = GetAuthorName();
            log.CommitterEmail = log.AuthorEmail = GetAuthorEmail();
            log.Date = changeset.CreationDate;
            log.Log = changeset.Comment + Environment.NewLine;
            log.ChangesetId = changeset.ChangesetId;
            return log;
        }

        private string GetAuthorEmail()
        {
            var identity = tfs.GetIdentity(changeset.Committer);
            return identity.MailAddress;
        }

        private string GetAuthorName()
        {
            var identity = tfs.GetIdentity(changeset.Committer);
            return identity.DisplayName;
        }
    }
}
