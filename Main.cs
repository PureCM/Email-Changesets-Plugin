using System;
using System.Collections.Generic;
using System.Text;
using PureCM.Server;
using PureCM.Client;
using System.Net;
using System.Net.Mail;
using System.Xml.Linq;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using DiffPlex;
using System.Web;
using System.Diagnostics;

namespace Plugin_EMailChangesets
{
    [EventHandlerDescription("Plugin that allows for emailing changeset descriptions to users")]
    public class EMailChangesetsPlugin : PureCM.Server.Plugin
    {
        public override bool OnStart( XElement oConfig, Connection conn)
        {
            LogInfo("Starting Email Plugin");

            m_oFromAddr = new MailAddress( oConfig.Element("From").Value );
            m_listRecipients = new List<MailAddress>();

            try
            {
                foreach (XElement recipient in oConfig.Element("Recipients").Elements("Recipient"))
                {
                    m_listRecipients.Add(new MailAddress(recipient.Value));
                }
            }
            catch (Exception e)
            {
                LogWarning(String.Format("Handled exception when parsing recipient list - {0}", e.Message));
            }

            if ( m_listRecipients.Count == 0 )
            {
                LogWarning("No recipients configured for EMailChangesets plugin");
                return false;
            }

            m_strSMTPServer = oConfig.Element("SMTPServer").Value;

            XElement oSMTPUser = oConfig.Element("SMTPUser");

            if ( oSMTPUser != null )
            {
                m_oCredentials = new NetworkCredential(oSMTPUser.Value, oConfig.Element("SMTPPassword").Value);
            }
            else
            {
                m_oCredentials = null;
            }

            m_strStyles = GetConfigString( oConfig,"CSS","");
            m_strSubjectFormat = GetConfigString(oConfig,"SubjectFormat","Changeset {0} submitted");
            m_nContextLines = int.Parse(oConfig.Element("ContextLines").Value);
            m_nMaxFileSizeForDiff = GetConfigInt(oConfig,"MaxFileSizeForDiff",500000);
            m_nMaxDiffLines = GetConfigInt(oConfig,"MaxDiffLines",1000);
            m_nMaxDiffLineLength = GetConfigInt(oConfig,"MaxDiffLineLength",500);

            try
            {
                m_oReposPattern = new Regex(oConfig.Element("ReposPattern").Value);
            }
            catch( Exception e )
            {
                LogWarning(String.Format("Invalid ReposPattern value for EMailChangesets plugin: {0}",e.Message));
                m_oReposPattern = null;
            }

            try
            {
                m_oStreamPattern = new Regex(oConfig.Element("StreamPattern").Value);
            }
            catch (Exception e)
            {
                LogWarning(String.Format("Invalid StreamPattern value for EMailChangesets plugin: {0}", e.Message));
                m_oStreamPattern = null;
            }

            m_bIncludeFeatureChanges = GetConfigBool(oConfig,"IncludeFeatureChangesets",false);
            m_bIncludeMergeChanges = GetConfigBool(oConfig, "IncludeMergeChangesets",false);
            m_bEnableTaskAssignNtfs = GetConfigBool(oConfig, "EnableTaskAssignNtfs", false);

            conn.OnChangeSubmitted = OnChangeSubmitted;
            conn.OnStreamCreated = OnStreamCreated;

            if (m_bEnableTaskAssignNtfs)
            {
                conn.OnProjectItemAssigned = OnProjectItemAssigned;
            }

            // Check the repository and stream patterns matches at least one repository and stream
            try
            {
                bool bFoundRepos = false;
                bool bFoundStream = false;

                if (m_oReposPattern != null)
                {
                    foreach (Repository oRepos in conn.Repositories)
                    {
                        if (m_oReposPattern.IsMatch(oRepos.Name))
                        {
                            bFoundRepos = true;

                            if ( m_oStreamPattern != null )
                            {
                                foreach (PureCM.Client.Stream oStream in oRepos.Streams)
                                {
                                    if (m_oStreamPattern.IsMatch(oStream.StreamPath))
                                    {
                                        bFoundStream = true;
                                        break;
                                    }
                                }
                            }
                        }

                        if (bFoundStream)
                        {
                            break;
                        }
                    }
                }

                if (!bFoundRepos)
                {
                    LogWarning(String.Format("There are no repositories matching the pattern: {0}", oConfig.Element("ReposPattern").Value));
                }
                else if (!bFoundStream)
                {
                    LogWarning(String.Format("There are no streams matching the pattern: {0}", oConfig.Element("StreamPattern").Value));
                }
            }
            catch (Exception e)
            {
                LogWarning(String.Format("Failed to check repository pattern: {0}", e.Message));
            }

            return true;
        }

        public override void OnStop()
        {
            LogInfo("Stopping Email Plugin");
        }

        private void OnStreamCreated(StreamCreatedEvent evt)
        {
            LogInfo("Email Plugin - OnStreamCreated()");

            if (evt.Repository != null)
            {
                evt.Repository.RefreshStreams();
            }
        }

        private void OnChangeSubmitted( ChangeSubmittedEvent evt )
        {
            LogInfo("Email Plugin - OnChangeSubmitted()");

            if (evt.Connection == null)
            {
                LogWarning("Unable to access connection in EMailChangesets plugin.");
            }
            else if (evt.Repository == null)
            {
                LogWarning("Unable to access repository '" + evt.RepositoryName + "' in EMailChangesets plugin.");

                Trace("Repositories include:");
                foreach (Repository oTmpRepos in evt.Connection.Repositories)
                {
                    Trace("  " + oTmpRepos.Name);
                }
                Trace("No more repositories.");
            }
            else if (evt.Stream == null)
            {
                LogWarning("Unable to access stream '" + evt.StreamName + "' in EMailChangesets plugin.");

                Trace("Streams include:");
                foreach (PureCM.Client.Stream oReposStream in evt.Repository.Streams)
                {
                    Trace("  " + oReposStream.Name);
                }
                Trace("No more streams.");
            }

            Changeset oCS = evt.Changeset;

            if ( oCS == null)
            {
                LogWarning("Unable to access Changeset in EMailChangesets plugin - check Group/Access rights to the repository for the plugin user.");
                return;
            }

            if (!m_bIncludeMergeChanges && oCS.IsMergeChange )
            {
                // Ignore merge changesets
                LogInfo(string.Format("Not sending changeset '{0}' because it is a merge as specified in the config file", oCS.IdString));
                return;
            }

            if (!m_bIncludeFeatureChanges)
            {
                if (oCS.Stream == null)
                {
                    LogWarning("Unable to access changeset stream in EMailChangesets plugin.");
                    return;
                }

                ProjectItem oProjectItem = oCS.Stream.ProjectItem;

                if (oProjectItem != null)
                {
                    if (oProjectItem.StreamDataType == SDK.TStreamDataType.pcmStreamTypeFeature)
                    {
                        // Ignore feature changesets
                        LogInfo(string.Format("Not sending changeset '{0}' because it is a feature as specified in the config file", oCS.IdString));
                        return;
                    }
                }
            }

            PureCM.Client.Stream oStream = oCS.Stream;
            Repository oRepos = oStream.Repository;

            if ( ( m_oReposPattern != null ) && !m_oReposPattern.IsMatch(oRepos.Name) )
            {
                LogInfo(string.Format("Not sending changeset '{0}' because the repository '{1}' is not specified in the config file (only '{2}')", oCS.IdString, oRepos.Name, m_oReposPattern));
                return;
            }

            if (( m_oStreamPattern != null ) && !m_oStreamPattern.IsMatch(oStream.StreamPath) )
            {
                LogInfo(string.Format("Not sending changeset '{0}' because the stream '{1}' is not specified in the config file (only '{2}')", oCS.IdString, oStream.StreamPath, m_oStreamPattern));
                return;
            }

            StringBuilder output = new StringBuilder();

            output.Append("<html><head><style type='text/css'>");
            output.Append(m_strStyles);
            output.Append("</style></head><body><table border='0'><tr><td class='hdrt' width='200'>Change Ref</td><td>");
            output.Append(HttpUtility.HtmlEncode(oCS.IdString));
            output.Append("</td></tr><tr><td class='hdrt' width='200'>Stream</td><td>");
            output.Append(HttpUtility.HtmlEncode(oCS.Stream.StreamPath));
            output.Append("</td></tr><tr><td class='hdrt' width='200'>Description</td><td>");
            output.Append(HttpUtility.HtmlEncode(oCS.Description));
            output.Append("</td></tr><tr><td class='hdrt' width='200'>Time</td><td>");
            output.Append(HttpUtility.HtmlEncode(oCS.Timestamp.ToString()));
            output.Append("</td></tr><tr><td class='hdrt' width='200'>Client</td><td>");
            output.Append(HttpUtility.HtmlEncode(oCS.ClientName));
            output.Append("</td></tr></table><hr/><table border='0' class='ci'><tr align='left'><th width='200' align='left'>Path</th><th width='100' align='left'>Type</th><th width='400' align='left'>Details</th></tr>");

            foreach ( ChangeItem oItem in oCS.Items )
            {
                output.Append("<tr valign='top'>");

                switch ( oItem.Type )
                {
                    case SDK.TPCMChangeItemType.pcmEdit:
                        output.Append("<td>");
                        output.Append(HttpUtility.HtmlEncode(oItem.Path));
                        output.Append("</td><td>Edit</td>");

                        if ((oItem.ServerNewRevision != null) && (oItem.ServerBaseRevision != null))
                        {
                            StreamFileRev oBaseRev = oItem.ServerBaseRevision;
                            if (!oBaseRev.FileType.IsBinary)
                            {
                                System.IO.Stream oBaseStream = oBaseRev.Content;
                                TextReader streamBaseReader = new StreamReader(oBaseStream);

                                String strOldText = streamBaseReader.ReadToEnd();

                                if (strOldText.Length < m_nMaxFileSizeForDiff)
                                {
                                    StreamFileRev oNewRev = oItem.ServerNewRevision;

                                    System.IO.Stream oNewStream = oNewRev.Content;
                                    TextReader streamNewReader = new StreamReader(oNewStream);

                                    String strNewText = streamNewReader.ReadToEnd();

                                    OutputRevisionDifferences(strOldText, strNewText, output);
                                }
                                else
                                {
                                    output.Append("<td>Revision too large for showing differences</td>");
                                }
                            }
                            else
                            {
                                output.Append("<td>Binary Revision - no differences shown</td>");
                            }
                        }
                        break;

                    case SDK.TPCMChangeItemType.pcmAdd:
                        output.Append("<td>");
                        output.Append( HttpUtility.HtmlEncode(oItem.Path));
                        output.Append("</td><td>Add</td>");
                        break;

                    case SDK.TPCMChangeItemType.pcmAddFolder:
                        output.Append("<td>");
                        output.Append( HttpUtility.HtmlEncode(oItem.Path));
                        output.Append("</td><td>Add Folder</td>");
                        break;

                    case SDK.TPCMChangeItemType.pcmDelete:
                        output.Append("<td>");
                        output.Append( HttpUtility.HtmlEncode(oItem.Path));
                        output.Append("</td><td>Delete</td>");
                        break;

                    case SDK.TPCMChangeItemType.pcmDeleteFolder:
                        output.Append("<td>");
                        output.Append( HttpUtility.HtmlEncode(oItem.Path));
                        output.Append("</td><td>Delete Folder</td>");
                        break;

                    default:
                        break;
                }

                output.Append("</tr>");
            }

            output.Append("</table></body></html>");

            SendEmail("", String.Format(m_strSubjectFormat, oCS.IdString, oStream.StreamPath), output.ToString());
        }

        private void SendEmail(string strRecipient, string strSubject, string strBody)
        {
            LogInfo(string.Format("Sending '{0}'", strSubject));

            var msg = new MailMessage();

            msg.From = m_oFromAddr;

            if (strRecipient.Length > 0)
            {
                LogInfo(string.Format("Sending to '{0}'", strRecipient));
                msg.To.Add(strRecipient);
            }
            else
            {
                foreach (MailAddress addr in m_listRecipients)
                {
                    LogInfo(string.Format("Sending to '{0}'", addr));
                    msg.To.Add(addr);
                }
            }

            msg.Subject = strSubject;
            msg.Body = strBody;
            msg.IsBodyHtml = true;

            var client = new SmtpClient(m_strSMTPServer);

            if ( m_oCredentials != null )
            {
                client.UseDefaultCredentials = false;
                client.Credentials = m_oCredentials;
            }

            client.Send(msg);
        }

        private void OutputRevisionDifferences(String strOldText, String strNewText, StringBuilder output)
        {
            DiffPlex.Differ oDiff = new Differ();
            int nLineCount = 0;

            DiffPlex.Model.DiffResult oRes = oDiff.CreateLineDiffs(strOldText, strNewText, false);

            output.Append("<td>");

            bool bFirstBlock = true;

            foreach (DiffPlex.Model.DiffBlock oBlock in oRes.DiffBlocks)
            {
                if (!bFirstBlock)
                {
                    output.Append("<hr/>");
                }
                else
                {
                    bFirstBlock = false;
                }

                if ( nLineCount > m_nMaxDiffLines )
                {
                    output.Append("<pre class='diffend'>Truncated output - too many lines</pre></td>");
                    return;
                }

                if (oBlock.DeleteCountA > 0)
                {
                    // Output some context lines before the removal/change

                    for (int nContextLine = -m_nContextLines; nContextLine < 0; nContextLine++)
                    {
                        int nActualLine = nContextLine + oBlock.DeleteStartA;

                        if ( (nActualLine >= 0) && ( nActualLine < oRes.PiecesOld.Length ) )
                        {
                            String strLine = EnsureMaxLineLength(oRes.PiecesOld[nActualLine]);

                            output.Append("<pre class='diffdel'>");
                            output.Append(String.Format("{0:00000}   ", nActualLine));
                            output.Append(HttpUtility.HtmlEncode(strLine));
                            output.Append("</pre>");
                            nLineCount++;
                        }
                    }

                    // Output the lines that have been removed/changed...

                    for (int nCount = 0; nCount < oBlock.DeleteCountA; nCount++)
                    {
                        String strLine = EnsureMaxLineLength(oRes.PiecesOld[oBlock.DeleteStartA + nCount]);

                        output.Append("<pre class='diffdel'>");
                        output.Append(String.Format("{0:00000} - ", oBlock.DeleteStartA + nCount));
                        output.Append(HttpUtility.HtmlEncode(strLine));
                        output.Append("</pre>");
                        nLineCount++;

                        if (nLineCount > m_nMaxDiffLines)
                        {
                            output.Append("<pre class='diffend'>Truncated output - too many lines</pre></td>");
                            return;
                        }
                    }

                    // Possibly output some context lines at the end of the removed/changed lines...

                    for (int nContextLine = 0; nContextLine < m_nContextLines; nContextLine++)
                    {
                        int nActualLine = nContextLine + oBlock.DeleteStartA + oBlock.DeleteCountA;

                        if (nActualLine < oRes.PiecesOld.Length)
                        {
                            String strLine = EnsureMaxLineLength(oRes.PiecesOld[nActualLine]);

                            output.Append("<pre class='diffdel'>");
                            output.Append(String.Format("{0:00000}   ", nActualLine));
                            output.Append(HttpUtility.HtmlEncode(strLine));
                            output.Append("</pre>");
                            nLineCount++;
                        }
                    }
                }

                if (oBlock.InsertCountB > 0)
                {
                    // Possibly output some context lines before the insert/change

                    for (int nContextLine = -m_nContextLines; nContextLine < 0; nContextLine++)
                    {
                        int nActualLine = nContextLine + oBlock.InsertStartB;

                        if ((nActualLine >= 0) && (nActualLine < oRes.PiecesOld.Length))
                        {
                            String strLine = EnsureMaxLineLength(oRes.PiecesNew[nActualLine]);

                            output.Append("<pre class='diffins'>");
                            output.Append(String.Format("{0:00000}   ", nActualLine));
                            output.Append(HttpUtility.HtmlEncode(strLine));
                            output.Append("</pre>");
                            nLineCount++;
                        }
                    }

                    // Output the lines that have actually been inserted/changes...

                    for (int nCount = 0; nCount < oBlock.InsertCountB; nCount++)
                    {
                        String strLine = EnsureMaxLineLength(oRes.PiecesNew[oBlock.InsertStartB + nCount]);

                        output.Append("<pre class='diffins'>");
                        output.Append(String.Format("{0:00000} + ", oBlock.InsertStartB + nCount));
                        output.Append(HttpUtility.HtmlEncode(strLine));
                        output.Append("</pre>");
                        nLineCount++;

                        if (nLineCount > m_nMaxDiffLines)
                        {
                            output.Append("<pre class='diffend'>Truncated output - too many lines</pre></td>");
                            return;
                        }
                    }

                    // Output context lines at the end of the insert...

                    for (int nContextLine = 0; nContextLine < m_nContextLines; nContextLine++)
                    {
                        int nActualLine = nContextLine + oBlock.InsertStartB + oBlock.InsertCountB;

                        if (nActualLine < oRes.PiecesNew.Length)
                        {
                            String strLine = EnsureMaxLineLength(oRes.PiecesNew[nActualLine]);

                            output.Append("<pre class='diffins'>");
                            output.Append(String.Format("{0:00000}   ", nActualLine));
                            output.Append(HttpUtility.HtmlEncode(strLine));
                            output.Append("</pre>");
                            nLineCount++;
                        }
                    }
                }
            }
            output.Append("</td>");
        }

        private String EnsureMaxLineLength( String strText )
        {
            if ( strText.Length > m_nMaxDiffLineLength )
            {
                return strText.Substring(0, m_nMaxDiffLineLength);
            }

            return strText;
        }

        private void OnProjectItemAssigned(ProjectItemEvent evt)
        {
            LogInfo(string.Format("Task {0} has been assigned", evt.ProjectItemID));

            if (!m_bEnableTaskAssignNtfs)
            {
                LogInfo("Task assignment notifications have not been enabled");
                return;
            }

            if ((m_oReposPattern != null) &&
                (evt.Repository != null) &&
                !m_oReposPattern.IsMatch(evt.Repository.Name))
            {
                LogInfo(string.Format("Task assignment notifications have not been enabled for repository '{0}' (only '{1}')", evt.Repository.Name, m_oReposPattern));
                return;
            }

            if (evt.NewID > 0)
            {
                UserOrGroup oUser = evt.Connection.Users.ById(evt.NewID);

                if (oUser != null && oUser.Email.Length > 0)
                {
                    ProjectItem oItem = evt.Repository.ProjectItemById(evt.ProjectItemID);

                    if (oItem != null)
                    {
                        string strSubject = string.Format("'{0}' has been assigned to you", oItem.Name);

                        SendEmail(oUser.Email, strSubject, oItem.Description);
                    }
                    else
                    {
                        LogInfo(string.Format("Project item with id {0} does not exist so no notification has been sent", evt.ProjectItemID));
                    }
                }
                else
                {
                    if (oUser == null)
                    {
                        LogInfo(string.Format("User with id {0} does not exist so no notification has been sent", evt.NewID));
                    }
                    else
                    {
                        LogInfo(string.Format("A notification will not be sent because the user '{0}' does not have an email address specified in PureCM", oUser.Name));
                    }
                }
            }
            else
            {
                LogInfo("The task has been unassigned so no notification has been sent");
            }
        }

        private bool GetConfigBool(XElement oConfig, String strName, bool bDefault)
        {
            XElement oElt = oConfig.Element(strName);
            bool bRet = bDefault;

            if ( (oElt != null) && ( oElt.Value != null ) )
            {
                bRet = bool.Parse(oElt.Value);
            }

            LogInfo(string.Format("Config '{0}'={1}", strName, bRet));

            return bRet;
        }

        private int GetConfigInt(XElement oConfig, String strName, int nDefault)
        {
            XElement oElt = oConfig.Element(strName);
            int nRet = nDefault;

            if ((oElt != null) && (oElt.Value != null))
            {
                nRet = int.Parse(oElt.Value);
            }
            return nRet;
        }

        private String GetConfigString(XElement oConfig, String strName, String strDefault)
        {
            XElement oElt = oConfig.Element(strName);
            String strRet = strDefault;

            if ((oElt != null) && (oElt.Value != null))
            {
                strRet = oElt.Value;
            }
            return strRet;
        }

        private MailAddress m_oFromAddr;
        private List<MailAddress> m_listRecipients;
        private String m_strSMTPServer;
        private NetworkCredential m_oCredentials;
        private String m_strStyles;
        private String m_strSubjectFormat;
        private int m_nContextLines;
        private int m_nMaxFileSizeForDiff;
        private int m_nMaxDiffLines;
        private int m_nMaxDiffLineLength;
        private Regex m_oReposPattern;
        private Regex m_oStreamPattern;
        private bool m_bIncludeMergeChanges;
        private bool m_bIncludeFeatureChanges;
        private bool m_bEnableTaskAssignNtfs;
    }
}
