using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using EliteSoft.MetaAdmin.Shared.Data.Entities;

namespace EliteSoft.Erwin.AddIn.Forms
{
    /// <summary>
    /// Wizard-style dialog for question-based property assignment.
    /// Shown when a new table is created — asks questions, collects answers,
    /// and returns property values to apply to the model.
    /// </summary>
    public class QuestionWizardForm : Form
    {
        private readonly string _tableName;
        private readonly List<QuestionDef> _allQuestions;
        private readonly List<QuestionStep> _steps = new List<QuestionStep>();
        private int _currentStepIndex = -1;
        private readonly dynamic _entity; // erwin entity for COLUMN_SELECT
        private readonly dynamic _session; // erwin session for SCAPI access

        // Collected answers: QuestionDefId -> answer value
        private readonly Dictionary<int, string> _answers = new Dictionary<int, string>();

        // Result: PropertyDefId -> value (populated from question rules)
        public Dictionary<int, string> PropertyValues { get; } = new Dictionary<int, string>();

        // UI Controls
        private Panel pnlHeader;
        private Label lblTitle;
        private Label lblSubtitle;
        private Panel pnlContent;
        private Label lblQuestionNumber;
        private Label lblQuestionText;
        private Panel pnlAnswerArea;
        private Panel pnlFooter;
        private Panel pnlSteps;
        private Button btnBack;
        private Button btnNext;
        private Label lblSummaryTitle;
        private ComboBox cmbColumnSelect; // For COLUMN_SELECT answer type

        // Design system
        private static readonly Color ClrPrimary = Color.FromArgb(0, 102, 204);
        private static readonly Color ClrSuccess = Color.FromArgb(0, 138, 62);
        private static readonly Color ClrTextPrimary = Color.FromArgb(26, 26, 26);
        private static readonly Color ClrTextSecondary = Color.FromArgb(102, 102, 102);
        private static readonly Color ClrBorder = Color.FromArgb(208, 208, 208);
        private static readonly Color ClrSurface = Color.FromArgb(245, 247, 250);

        public QuestionWizardForm(string tableName, List<QuestionDef> questions, dynamic session = null, dynamic entity = null)
        {
            _tableName = tableName;
            _allQuestions = questions;
            _session = session;
            _entity = entity;
            InitializeUI();
            BuildSteps();
            ShowStep(0);
        }

        #region UI Setup

        private void InitializeUI()
        {
            this.Text = "Table Properties Wizard";
            this.Size = new Size(560, 420);
            this.MinimumSize = new Size(480, 380);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.ShowInTaskbar = false;
            this.BackColor = Color.White;
            this.Font = new Font("Segoe UI", 9.5F);

            // Header panel
            pnlHeader = new Panel
            {
                Dock = DockStyle.Top,
                Height = 70,
                BackColor = Color.White,
                Padding = new Padding(20, 12, 20, 8)
            };

            lblTitle = new Label
            {
                Text = $"New Table: {_tableName}",
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                ForeColor = ClrTextPrimary,
                AutoSize = true,
                Location = new Point(20, 12)
            };

            lblSubtitle = new Label
            {
                Text = "Answer the following questions to configure table properties.",
                Font = new Font("Segoe UI", 9.5F),
                ForeColor = ClrTextSecondary,
                AutoSize = true,
                Location = new Point(20, 42)
            };

            pnlHeader.Controls.Add(lblTitle);
            pnlHeader.Controls.Add(lblSubtitle);

            // Header separator
            var headerSep = new Panel
            {
                Dock = DockStyle.Top,
                Height = 1,
                BackColor = ClrBorder
            };

            // Step indicator panel
            pnlSteps = new Panel
            {
                Dock = DockStyle.Top,
                Height = 32,
                BackColor = ClrSurface,
                Padding = new Padding(20, 6, 20, 6)
            };

            // Content panel
            pnlContent = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.White,
                Padding = new Padding(28, 20, 28, 10)
            };

            lblQuestionNumber = new Label
            {
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = ClrPrimary,
                AutoSize = true,
                Location = new Point(28, 20)
            };

            lblQuestionText = new Label
            {
                Font = new Font("Segoe UI", 11F, FontStyle.Regular),
                ForeColor = ClrTextPrimary,
                Location = new Point(28, 42),
                Size = new Size(480, 50),
                AutoSize = false
            };

            pnlAnswerArea = new Panel
            {
                Location = new Point(28, 100),
                Size = new Size(480, 160),
                AutoScroll = true
            };

            lblSummaryTitle = new Label
            {
                Font = new Font("Segoe UI", 11F, FontStyle.Bold),
                ForeColor = ClrTextPrimary,
                Text = "Summary",
                AutoSize = true,
                Location = new Point(28, 20),
                Visible = false
            };

            pnlContent.Controls.Add(lblQuestionNumber);
            pnlContent.Controls.Add(lblQuestionText);
            pnlContent.Controls.Add(pnlAnswerArea);
            pnlContent.Controls.Add(lblSummaryTitle);

            // Footer separator
            var footerSep = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 1,
                BackColor = ClrBorder
            };

            // Footer panel
            pnlFooter = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 50,
                BackColor = ClrSurface,
                Padding = new Padding(16, 8, 16, 8)
            };

            // Back — Secondary style
            btnBack = new Button
            {
                Text = "< Back",
                Size = new Size(100, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                ForeColor = ClrTextPrimary,
                Font = new Font("Segoe UI", 9.5F),
                Enabled = false,
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            btnBack.FlatAppearance.BorderColor = ClrBorder;
            btnBack.Click += BtnBack_Click;

            // Next — Primary style
            btnNext = new Button
            {
                Text = "Next >",
                Size = new Size(110, 32),
                FlatStyle = FlatStyle.Flat,
                BackColor = ClrPrimary,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            btnNext.FlatAppearance.BorderSize = 0;
            btnNext.Click += BtnNext_Click;

            // Position buttons — only Back + Next
            btnNext.Location = new Point(pnlFooter.Width - 126, 9);
            btnBack.Location = new Point(btnNext.Left - 108, 9);

            pnlFooter.Controls.AddRange(new Control[] { btnBack, btnNext });

            // Add controls in reverse dock order
            this.Controls.Add(pnlContent);
            this.Controls.Add(pnlSteps);
            this.Controls.Add(headerSep);
            this.Controls.Add(pnlHeader);
            this.Controls.Add(footerSep);
            this.Controls.Add(pnlFooter);

            // Reposition buttons on resize
            pnlFooter.Resize += (s, e) =>
            {
                btnNext.Location = new Point(pnlFooter.Width - 126, 9);
                btnBack.Location = new Point(btnNext.Left - 108, 9);
            };
        }

        #endregion

        #region Step Management

        private void BuildSteps()
        {
            // Root questions (no parent dependency)
            var rootQuestions = _allQuestions
                .Where(q => q.DependsOnQuestionDefId == null)
                .OrderBy(q => q.SortOrder)
                .ToList();

            foreach (var q in rootQuestions)
            {
                _steps.Add(new QuestionStep { Question = q });
            }
        }

        private void ShowStep(int index)
        {
            if (index < 0 || index >= _steps.Count)
                return;

            _currentStepIndex = index;
            var step = _steps[index];

            // Update step indicator
            UpdateStepIndicator();

            // Show question
            lblSummaryTitle.Visible = false;
            lblQuestionNumber.Visible = true;
            lblQuestionText.Visible = true;
            pnlAnswerArea.Visible = true;

            lblQuestionNumber.Text = $"QUESTION {index + 1} OF {_steps.Count}";
            lblQuestionText.Text = step.Question.QuestionText;

            // Build answer controls
            pnlAnswerArea.Controls.Clear();
            cmbColumnSelect = null;

            if (step.Question.AnswerType == "YES_NO")
            {
                BuildYesNoAnswer(step);
            }
            else if (step.Question.AnswerType == "SINGLE_SELECT")
            {
                BuildSingleSelectAnswer(step);
            }
            else if (step.Question.AnswerType == "COLUMN_SELECT")
            {
                BuildColumnSelectAnswer(step);
            }

            // Button states
            btnBack.Enabled = index > 0;
            btnNext.Text = "Next >";

            // Restore previous answer selection if going back
            if (_answers.ContainsKey(step.Question.Id))
            {
                RestoreAnswer(step, _answers[step.Question.Id]);
            }

            UpdateNextButtonState();
        }

        private void ShowSummary()
        {
            lblQuestionNumber.Visible = false;
            lblQuestionText.Visible = false;
            pnlAnswerArea.Visible = false;
            lblSummaryTitle.Visible = true;

            // Clear and rebuild answer area as summary
            pnlAnswerArea.Controls.Clear();
            pnlAnswerArea.Location = new Point(28, 48);
            pnlAnswerArea.Size = new Size(480, 200);
            pnlAnswerArea.Visible = true;

            int y = 0;
            foreach (var step in _steps)
            {
                if (!_answers.ContainsKey(step.Question.Id))
                    continue;

                var answer = _answers[step.Question.Id];
                string displayAnswer = GetDisplayAnswer(step.Question, answer);

                var lblQ = new Label
                {
                    Text = step.Question.QuestionText,
                    Font = new Font("Segoe UI", 8.5F),
                    ForeColor = ClrTextSecondary,
                    AutoSize = true,
                    Location = new Point(0, y)
                };

                var lblA = new Label
                {
                    Text = displayAnswer,
                    Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                    ForeColor = ClrTextPrimary,
                    AutoSize = true,
                    Location = new Point(12, y + 18)
                };

                pnlAnswerArea.Controls.Add(lblQ);
                pnlAnswerArea.Controls.Add(lblA);
                y += 44;
            }

            // Show property assignments
            if (PropertyValues.Count > 0)
            {
                var lblProps = new Label
                {
                    Text = $"{PropertyValues.Count} property(s) will be applied.",
                    Font = new Font("Segoe UI", 9.5F),
                    ForeColor = ClrSuccess,
                    AutoSize = true,
                    Location = new Point(0, y + 10)
                };
                pnlAnswerArea.Controls.Add(lblProps);
            }

            btnNext.Text = "OK";
            btnBack.Enabled = _steps.Count > 0;

            UpdateStepIndicator();
        }

        private void UpdateStepIndicator()
        {
            pnlSteps.Controls.Clear();
            int x = 20;
            const int maxVisible = 8;

            // Determine visible range (keep current step centered when too many steps)
            int totalSteps = _steps.Count;
            int startIdx = 0;
            int endIdx = totalSteps - 1;

            if (totalSteps > maxVisible)
            {
                int half = maxVisible / 2;
                startIdx = Math.Max(0, _currentStepIndex - half);
                endIdx = startIdx + maxVisible - 1;
                if (endIdx >= totalSteps)
                {
                    endIdx = totalSteps - 1;
                    startIdx = endIdx - maxVisible + 1;
                }
            }

            // Leading ellipsis
            if (startIdx > 0)
            {
                var ellipsis = new Label
                {
                    Text = "...",
                    Size = new Size(22, 22),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 8F),
                    ForeColor = ClrTextSecondary,
                    Location = new Point(x, 5)
                };
                pnlSteps.Controls.Add(ellipsis);
                x += 30;
            }

            for (int i = startIdx; i <= endIdx; i++)
            {
                bool isCurrent = i == _currentStepIndex;
                bool isCompleted = _answers.ContainsKey(_steps[i].Question.Id);

                var indicator = new Label
                {
                    Text = (i + 1).ToString(),
                    Size = new Size(22, 22),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                    Location = new Point(x, 5)
                };

                if (isCurrent)
                {
                    indicator.BackColor = ClrPrimary;
                    indicator.ForeColor = Color.White;
                }
                else if (isCompleted)
                {
                    indicator.BackColor = ClrSuccess;
                    indicator.ForeColor = Color.White;
                    indicator.Text = "\u2713";
                }
                else
                {
                    indicator.BackColor = ClrBorder;
                    indicator.ForeColor = ClrTextSecondary;
                }

                pnlSteps.Controls.Add(indicator);

                // Connector line
                if (i < endIdx)
                {
                    var connector = new Panel
                    {
                        Size = new Size(20, 2),
                        BackColor = isCompleted ? ClrSuccess : ClrBorder,
                        Location = new Point(x + 24, 15)
                    };
                    pnlSteps.Controls.Add(connector);
                }

                x += 46;
            }

            // Trailing ellipsis
            if (endIdx < totalSteps - 1)
            {
                x += 4;
                var ellipsis = new Label
                {
                    Text = "...",
                    Size = new Size(22, 22),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 8F),
                    ForeColor = ClrTextSecondary,
                    Location = new Point(x, 5)
                };
                pnlSteps.Controls.Add(ellipsis);
                x += 30;
            }
            else
            {
                // Connector to summary
                var connector = new Panel
                {
                    Size = new Size(20, 2),
                    BackColor = (_currentStepIndex >= totalSteps) ? ClrSuccess : ClrBorder,
                    Location = new Point(x - 22 + 24, 15)
                };
                pnlSteps.Controls.Add(connector);
            }

            // Summary indicator (always last)
            bool isSummary = _currentStepIndex >= _steps.Count;
            var summaryIndicator = new Label
            {
                Text = isSummary ? "\u2713" : "S",
                Size = new Size(22, 22),
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold),
                BackColor = isSummary ? ClrPrimary : ClrBorder,
                ForeColor = isSummary ? Color.White : ClrTextSecondary,
                Location = new Point(x, 5)
            };
            pnlSteps.Controls.Add(summaryIndicator);
        }

        #endregion

        #region Answer UI Builders

        private void BuildYesNoAnswer(QuestionStep step)
        {
            var rbYes = new RadioButton
            {
                Text = "Yes",
                Font = new Font("Segoe UI", 10F),
                ForeColor = ClrTextPrimary,
                AutoSize = true,
                Location = new Point(12, 10),
                Tag = "YES",
                Padding = new Padding(6)
            };

            var rbNo = new RadioButton
            {
                Text = "No",
                Font = new Font("Segoe UI", 10F),
                ForeColor = ClrTextPrimary,
                AutoSize = true,
                Location = new Point(12, 46),
                Tag = "NO",
                Padding = new Padding(6)
            };

            rbYes.CheckedChanged += (s, e) => { if (rbYes.Checked) UpdateNextButtonState(); };
            rbNo.CheckedChanged += (s, e) => { if (rbNo.Checked) UpdateNextButtonState(); };

            pnlAnswerArea.Controls.Add(rbYes);
            pnlAnswerArea.Controls.Add(rbNo);
        }

        private void BuildSingleSelectAnswer(QuestionStep step)
        {
            var options = step.Question.QuestionOptions?
                .OrderBy(o => o.SortOrder)
                .ToList() ?? new List<QuestionOption>();

            int y = 10;
            foreach (var opt in options)
            {
                string displayText = !string.IsNullOrEmpty(opt.DisplayText) ? opt.DisplayText : opt.Value;

                var rb = new RadioButton
                {
                    Text = displayText,
                    Font = new Font("Segoe UI", 10F),
                    ForeColor = ClrTextPrimary,
                    AutoSize = true,
                    Location = new Point(12, y),
                    Tag = opt.Value,
                    Padding = new Padding(6)
                };

                rb.CheckedChanged += (s, e) => { if (rb.Checked) UpdateNextButtonState(); };
                pnlAnswerArea.Controls.Add(rb);
                y += 38;
            }
        }

        private void BuildColumnSelectAnswer(QuestionStep step)
        {
            var lblHint = new Label
            {
                Text = "Select a column from the table:",
                Font = new Font("Segoe UI", 9F),
                ForeColor = ClrTextSecondary,
                AutoSize = true,
                Location = new Point(12, 10)
            };

            cmbColumnSelect = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("Segoe UI", 10F),
                Location = new Point(12, 34),
                Size = new Size(350, 30)
            };

            // Load columns from entity via SCAPI
            if (_entity != null && _session != null)
            {
                try
                {
                    dynamic modelObjects = _session.ModelObjects;
                    dynamic attributes = modelObjects.Collect(_entity, "Attribute");
                    if (attributes != null)
                    {
                        for (int i = 0; i < attributes.Count; i++)
                        {
                            try
                            {
                                dynamic attr = attributes.Item(i);
                                string physName = attr.Properties("Physical_Name").Value?.ToString();
                                if (!string.IsNullOrEmpty(physName))
                                    cmbColumnSelect.Items.Add(physName);
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }

            if (cmbColumnSelect.Items.Count == 0)
            {
                lblHint.Text = "No columns found on this table. Type the column name:";
                cmbColumnSelect.DropDownStyle = ComboBoxStyle.DropDown;
            }

            cmbColumnSelect.SelectedIndexChanged += (s, e) => UpdateNextButtonState();
            cmbColumnSelect.TextChanged += (s, e) => UpdateNextButtonState();

            pnlAnswerArea.Controls.Add(lblHint);
            pnlAnswerArea.Controls.Add(cmbColumnSelect);
        }

        private void RestoreAnswer(QuestionStep step, string answerValue)
        {
            if (step.Question.AnswerType == "COLUMN_SELECT" && cmbColumnSelect != null)
            {
                int idx = cmbColumnSelect.Items.IndexOf(answerValue);
                if (idx >= 0)
                    cmbColumnSelect.SelectedIndex = idx;
                else
                    cmbColumnSelect.Text = answerValue;
                return;
            }

            foreach (Control c in pnlAnswerArea.Controls)
            {
                if (c is RadioButton rb && rb.Tag?.ToString() == answerValue)
                {
                    rb.Checked = true;
                    break;
                }
            }
        }

        private string GetSelectedAnswer()
        {
            // Check COLUMN_SELECT ComboBox first
            if (cmbColumnSelect != null && cmbColumnSelect.Visible)
            {
                string colVal = cmbColumnSelect.SelectedItem?.ToString() ?? cmbColumnSelect.Text;
                return string.IsNullOrWhiteSpace(colVal) ? null : colVal.Trim();
            }

            foreach (Control c in pnlAnswerArea.Controls)
            {
                if (c is RadioButton rb && rb.Checked)
                    return rb.Tag?.ToString();
            }
            return null;
        }

        private string GetDisplayAnswer(QuestionDef question, string value)
        {
            if (question.AnswerType == "YES_NO")
                return value == "YES" ? "Yes" : "No";

            if (question.AnswerType == "COLUMN_SELECT")
                return value;

            var option = question.QuestionOptions?.FirstOrDefault(o => o.Value == value);
            return option?.DisplayText ?? option?.Value ?? value;
        }

        private void UpdateNextButtonState()
        {
            string selected = GetSelectedAnswer();
            btnNext.Enabled = !string.IsNullOrEmpty(selected);
        }

        #endregion

        #region Navigation

        private void BtnNext_Click(object sender, EventArgs e)
        {
            // Summary screen -> Apply
            if (_currentStepIndex >= _steps.Count)
            {
                DialogResult = DialogResult.OK;
                Close();
                return;
            }

            // Collect answer
            string answer = GetSelectedAnswer();
            if (string.IsNullOrEmpty(answer))
                return;

            var currentStep = _steps[_currentStepIndex];
            _answers[currentStep.Question.Id] = answer;

            // Apply rules for this answer
            ApplyRulesForAnswer(currentStep.Question.Id, answer);

            // Check for dependent sub-questions that should be inserted
            InsertSubQuestions(currentStep.Question.Id, answer);

            // Move to next step or summary
            if (_currentStepIndex + 1 < _steps.Count)
            {
                ShowStep(_currentStepIndex + 1);
            }
            else
            {
                _currentStepIndex = _steps.Count; // past last step
                ShowSummary();
            }
        }

        private void BtnBack_Click(object sender, EventArgs e)
        {
            if (_currentStepIndex >= _steps.Count)
            {
                // From summary, go back to last step
                ShowStep(_steps.Count - 1);
                return;
            }

            if (_currentStepIndex > 0)
            {
                // 1. Remove current step's answer and sub-questions (if answered)
                var currentStep = _steps[_currentStepIndex];
                if (_answers.ContainsKey(currentStep.Question.Id))
                {
                    RemoveSubQuestions(currentStep.Question.Id);
                    RemoveRulesForAnswer(currentStep.Question.Id);
                    _answers.Remove(currentStep.Question.Id);
                }

                // 2. Go to previous step — remove its sub-questions and answer too
                //    so user can re-answer it (sub-questions will be re-inserted on Next)
                int prevIdx = _currentStepIndex - 1;
                var prevStep = _steps[prevIdx];
                RemoveSubQuestions(prevStep.Question.Id);
                if (_answers.ContainsKey(prevStep.Question.Id))
                {
                    RemoveRulesForAnswer(prevStep.Question.Id);
                    _answers.Remove(prevStep.Question.Id);
                }

                ShowStep(prevIdx);
            }
        }

        #endregion

        #region Rule Processing

        private void ApplyRulesForAnswer(int questionDefId, string answerValue)
        {
            var question = _allQuestions.FirstOrDefault(q => q.Id == questionDefId);
            if (question?.QuestionRules == null) return;

            // Match exact answer (case-insensitive) OR wildcard '*'
            var matchingRules = question.QuestionRules
                .Where(r => string.Equals(r.AnswerValue, answerValue, StringComparison.OrdinalIgnoreCase) || r.AnswerValue == "*")
                .ToList();

            foreach (var rule in matchingRules)
            {
                // {ANSWER} placeholder: replace with actual answer value
                string propertyValue = rule.PropertyValue;
                if (propertyValue.Contains("{ANSWER}"))
                    propertyValue = propertyValue.Replace("{ANSWER}", answerValue);

                PropertyValues[rule.PropertyDefId] = propertyValue;
            }
        }

        private void RemoveRulesForAnswer(int questionDefId)
        {
            var question = _allQuestions.FirstOrDefault(q => q.Id == questionDefId);
            if (question?.QuestionRules == null) return;

            foreach (var rule in question.QuestionRules)
            {
                PropertyValues.Remove(rule.PropertyDefId);
            }
        }

        private void InsertSubQuestions(int parentQuestionId, string answer)
        {
            // Find sub-questions that depend on this answer
            var subQuestions = _allQuestions
                .Where(q => q.DependsOnQuestionDefId == parentQuestionId
                         && string.Equals(q.DependsOnAnswer, answer, StringComparison.OrdinalIgnoreCase))
                .OrderBy(q => q.SortOrder)
                .ToList();

            if (!subQuestions.Any()) return;

            // Insert after current step
            int insertIndex = _currentStepIndex + 1;
            foreach (var sq in subQuestions)
            {
                // Avoid duplicates
                if (_steps.Any(s => s.Question.Id == sq.Id))
                    continue;

                _steps.Insert(insertIndex, new QuestionStep
                {
                    Question = sq,
                    ParentQuestionId = parentQuestionId
                });
                insertIndex++;
            }
        }

        private void RemoveSubQuestions(int parentQuestionId)
        {
            // Remove all steps that were added as sub-questions of this parent
            var toRemove = _steps
                .Where(s => s.ParentQuestionId == parentQuestionId)
                .ToList();

            foreach (var step in toRemove)
            {
                // Also remove their sub-questions recursively
                RemoveSubQuestions(step.Question.Id);

                // Remove answer and rules
                if (_answers.ContainsKey(step.Question.Id))
                {
                    RemoveRulesForAnswer(step.Question.Id);
                    _answers.Remove(step.Question.Id);
                }

                _steps.Remove(step);
            }
        }

        #endregion

        private class QuestionStep
        {
            public QuestionDef Question { get; set; }
            public int? ParentQuestionId { get; set; }
        }
    }
}
