namespace HL.Manager
{
	using HL.HighlightingTheme;
	using ICSharpCode.AvalonEdit.Highlighting;
	using ICSharpCode.AvalonEdit.Highlighting.Xshd;
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Linq;
	using System.Runtime.Serialization;
	using System.Text;
	using System.Text.RegularExpressions;

	[Serializable]
	sealed class XmlHighlightingDefinition : IHighlightingDefinition
	{
		public string Name { get; private set; }

		public XmlHighlightingDefinition(XshdSyntaxDefinition xshd,
										 IHighlightingDefinitionReferenceResolver resolver)
		{
			InitializeDefinitions(xshd, resolver);
		}

		/// <summary>
		/// Class constructor from highlighting theme definition resolver
		/// and highlighting definition (and resolver)
		/// </summary>
		/// <param name="xshd"></param>
		/// <param name="resolver"></param>
		/// <param name="themedHighlights"></param>
		public XmlHighlightingDefinition(SyntaxDefinition themedHighlights,
										 XshdSyntaxDefinition xshd,
										 IHighlightingDefinitionReferenceResolver resolver
										 )
		{
			_themedHighlights = themedHighlights;
			InitializeDefinitions(xshd, resolver);
		}

		#region RegisterNamedElements
		sealed class RegisterNamedElementsVisitor : IXshdVisitor
		{
			XmlHighlightingDefinition def;
			internal readonly Dictionary<XshdRuleSet, HighlightingRuleSet> ruleSets
				= new Dictionary<XshdRuleSet, HighlightingRuleSet>();

			public RegisterNamedElementsVisitor(XmlHighlightingDefinition def)
			{
				this.def = def;
			}

			public object VisitRuleSet(XshdRuleSet ruleSet)
			{
				HighlightingRuleSet hrs = new HighlightingRuleSet();
				ruleSets.Add(ruleSet, hrs);
				if (ruleSet.Name != null)
				{
					if (ruleSet.Name.Length == 0)
						throw Error(ruleSet, "Name must not be the empty string");
					if (def.ruleSetDict.ContainsKey(ruleSet.Name))
						throw Error(ruleSet, "Duplicate rule set name '" + ruleSet.Name + "'.");

					def.ruleSetDict.Add(ruleSet.Name, hrs);
				}
				ruleSet.AcceptElements(this);
				return null;
			}

			public object VisitColor(XshdColor color)
			{
				if (color.Name != null)
				{
					if (color.Name.Length == 0)
						throw Error(color, "Name must not be the empty string");

					if (def.colorDict.ContainsKey(color.Name))
						throw Error(color, "Duplicate color name '" + color.Name + "'.");

					def.colorDict.Add(color.Name, new HighlightingColor());
				}
				return null;
			}

			public object VisitKeywords(XshdKeywords keywords)
			{
				return keywords.ColorReference.AcceptVisitor(this);
			}

			public object VisitSpan(XshdSpan span)
			{
				span.BeginColorReference.AcceptVisitor(this);
				span.SpanColorReference.AcceptVisitor(this);
				span.EndColorReference.AcceptVisitor(this);
				return span.RuleSetReference.AcceptVisitor(this);
			}

			public object VisitImport(XshdImport import)
			{
				return import.RuleSetReference.AcceptVisitor(this);
			}

			public object VisitRule(XshdRule rule)
			{
				return rule.ColorReference.AcceptVisitor(this);
			}
		}
		#endregion

		#region TranslateElements
		sealed class TranslateElementVisitor : IXshdVisitor
		{
			readonly XmlHighlightingDefinition def;
			readonly Dictionary<XshdRuleSet, HighlightingRuleSet> ruleSetDict;
			readonly Dictionary<HighlightingRuleSet, XshdRuleSet> reverseRuleSetDict;
			readonly IHighlightingDefinitionReferenceResolver resolver;
			HashSet<XshdRuleSet> processingStartedRuleSets = new HashSet<XshdRuleSet>();
			HashSet<XshdRuleSet> processedRuleSets = new HashSet<XshdRuleSet>();
			bool ignoreCase;

			public TranslateElementVisitor(XmlHighlightingDefinition def, Dictionary<XshdRuleSet, HighlightingRuleSet> ruleSetDict, IHighlightingDefinitionReferenceResolver resolver)
			{
				Debug.Assert(def != null);
				Debug.Assert(ruleSetDict != null);
				this.def = def;
				this.ruleSetDict = ruleSetDict;
				this.resolver = resolver;
				reverseRuleSetDict = new Dictionary<HighlightingRuleSet, XshdRuleSet>();
				foreach (var pair in ruleSetDict)
				{
					reverseRuleSetDict.Add(pair.Value, pair.Key);
				}
			}

			public object VisitRuleSet(XshdRuleSet ruleSet)
			{
				HighlightingRuleSet rs = ruleSetDict[ruleSet];
				if (processedRuleSets.Contains(ruleSet))
					return rs;
				if (!processingStartedRuleSets.Add(ruleSet))
					throw Error(ruleSet, "RuleSet cannot be processed because it contains cyclic <Import>");

				bool oldIgnoreCase = ignoreCase;
				if (ruleSet.IgnoreCase != null)
					ignoreCase = ruleSet.IgnoreCase.Value;

				rs.Name = ruleSet.Name;

				foreach (XshdElement element in ruleSet.Elements)
				{
					object o = element.AcceptVisitor(this);
					HighlightingRuleSet elementRuleSet = o as HighlightingRuleSet;
					if (elementRuleSet != null)
					{
						Merge(rs, elementRuleSet);
					}
					else
					{
						HighlightingSpan span = o as HighlightingSpan;
						if (span != null)
						{
							rs.Spans.Add(span);
						}
						else
						{
							HighlightingRule elementRule = o as HighlightingRule;
							if (elementRule != null)
							{
								rs.Rules.Add(elementRule);
							}
						}
					}
				}

				ignoreCase = oldIgnoreCase;
				processedRuleSets.Add(ruleSet);

				return rs;
			}

			static void Merge(HighlightingRuleSet target, HighlightingRuleSet source)
			{
				target.Rules.AddRange(source.Rules);
				target.Spans.AddRange(source.Spans);
			}

			public object VisitColor(XshdColor color)
			{
				HighlightingColor c = null;

				if (def._themedHighlights == null)
				{
					if (color.Name != null)
						c = def.colorDict[color.Name];
					else if (color.Foreground == null && color.Background == null && color.Underline == null && color.FontStyle == null && color.FontWeight == null)
						return null;
					else
						c = new HighlightingColor();

					c.Name = color.Name;
					c.Foreground = color.Foreground;
					c.Background = color.Background;
					c.Underline = color.Underline;
					c.FontStyle = color.FontStyle;
					c.FontWeight = color.FontWeight;
                    c.FontSize = color.FontSize;
					c.FontFamily = color.FontFamily;
                    c.Strikethrough = color.Strikethrough;
                    
                }

				return c;
			}

			public object VisitKeywords(XshdKeywords keywords)
			{
				if (keywords.Words.Count == 0)
					return Error(keywords, "Keyword group must not be empty.");
				foreach (string keyword in keywords.Words)
				{
					if (string.IsNullOrEmpty(keyword))
						throw Error(keywords, "Cannot use empty string as keyword");
				}
				StringBuilder keyWordRegex = new StringBuilder();
				// We can use "\b" only where the keyword starts/ends with a letter or digit, otherwise we don't
				// highlight correctly. (example: ILAsm-Mode.xshd with ".maxstack" keyword)
				if (keywords.Words.All(IsSimpleWord))
				{
					keyWordRegex.Append(@"\b(?>");
					// (?> = atomic group
					// atomic groups increase matching performance, but we
					// must ensure that the keywords are sorted correctly.
					// "\b(?>in|int)\b" does not match "int" because the atomic group captures "in".
					// To solve this, we are sorting the keywords by descending length.
					int i = 0;
					foreach (string keyword in keywords.Words.OrderByDescending(w => w.Length))
					{
						if (i++ > 0)
							keyWordRegex.Append('|');
						keyWordRegex.Append(Regex.Escape(keyword));
					}
					keyWordRegex.Append(@")\b");
				}
				else
				{
					keyWordRegex.Append('(');
					int i = 0;
					foreach (string keyword in keywords.Words)
					{
						if (i++ > 0)
							keyWordRegex.Append('|');
						if (char.IsLetterOrDigit(keyword[0]))
							keyWordRegex.Append(@"\b");
						keyWordRegex.Append(Regex.Escape(keyword));
						if (char.IsLetterOrDigit(keyword[keyword.Length - 1]))
							keyWordRegex.Append(@"\b");
					}
					keyWordRegex.Append(')');
				}
				return new HighlightingRule
				{
					Color = GetColor(keywords, keywords.ColorReference),
					Regex = CreateRegex(keywords, keyWordRegex.ToString(), XshdRegexType.Default)
				};
			}

			static bool IsSimpleWord(string word)
			{
				return char.IsLetterOrDigit(word[0]) && char.IsLetterOrDigit(word, word.Length - 1);
			}

			Regex CreateRegex(XshdElement position, string regex, XshdRegexType regexType)
			{
				if (regex == null)
					throw Error(position, "Regex missing");
				RegexOptions options = RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture;
				if (regexType == XshdRegexType.IgnorePatternWhitespace)
					options |= RegexOptions.IgnorePatternWhitespace;
				if (ignoreCase)
					options |= RegexOptions.IgnoreCase;
				try
				{
					return new Regex(regex, options);
				}
				catch (ArgumentException ex)
				{
					throw Error(position, ex.Message);
				}
			}

			HighlightingColor GetColor(XshdElement position, XshdReference<XshdColor> colorReference)
			{
				if (colorReference.InlineElement != null)
				{
					return (HighlightingColor)colorReference.InlineElement.AcceptVisitor(this);
				}
				else if (colorReference.ReferencedElement != null)
				{
					IHighlightingDefinition definition = GetDefinition(position, colorReference.ReferencedDefinition);
					HighlightingColor color = definition.GetNamedColor(colorReference.ReferencedElement);
					if (color == null)
						throw Error(position, "Could not find color named '" + colorReference.ReferencedElement + "'.");
					return color;
				}
				else
				{
					return null;
				}
			}

			IHighlightingDefinition GetDefinition(XshdElement position, string definitionName)
			{
				if (definitionName == null)
					return def;
				if (resolver == null)
					throw Error(position, "Resolving references to other syntax definitions is not possible because the IHighlightingDefinitionReferenceResolver is null.");
				IHighlightingDefinition d = resolver.GetDefinition(definitionName);
				if (d == null)
					throw Error(position, "Could not find definition with name '" + definitionName + "'.");
				return d;
			}

			HighlightingRuleSet GetRuleSet(XshdElement position, XshdReference<XshdRuleSet> ruleSetReference)
			{
				if (ruleSetReference.InlineElement != null)
				{
					return (HighlightingRuleSet)ruleSetReference.InlineElement.AcceptVisitor(this);
				}
				else if (ruleSetReference.ReferencedElement != null)
				{
					IHighlightingDefinition definition = GetDefinition(position, ruleSetReference.ReferencedDefinition);
					HighlightingRuleSet ruleSet = definition.GetNamedRuleSet(ruleSetReference.ReferencedElement);
					if (ruleSet == null)
						throw Error(position, "Could not find rule set named '" + ruleSetReference.ReferencedElement + "'.");
					return ruleSet;
				}
				else
				{
					return null;
				}
			}

			public object VisitSpan(XshdSpan span)
			{
				string endRegex = span.EndRegex;
				if (string.IsNullOrEmpty(span.BeginRegex) && string.IsNullOrEmpty(span.EndRegex))
					throw Error(span, "Span has no start/end regex.");
				if (!span.Multiline)
				{
					if (endRegex == null)
						endRegex = "$";
					else if (span.EndRegexType == XshdRegexType.IgnorePatternWhitespace)
						endRegex = "($|" + endRegex + "\n)";
					else
						endRegex = "($|" + endRegex + ")";
				}
				HighlightingColor wholeSpanColor = GetColor(span, span.SpanColorReference);
				return new HighlightingSpan
				{
					StartExpression = CreateRegex(span, span.BeginRegex, span.BeginRegexType),
					EndExpression = CreateRegex(span, endRegex, span.EndRegexType),
					RuleSet = GetRuleSet(span, span.RuleSetReference),
					StartColor = GetColor(span, span.BeginColorReference),
					SpanColor = wholeSpanColor,
					EndColor = GetColor(span, span.EndColorReference),
					SpanColorIncludesStart = true,
					SpanColorIncludesEnd = true
				};
			}

			public object VisitImport(XshdImport import)
			{
				HighlightingRuleSet hrs = GetRuleSet(import, import.RuleSetReference);
				XshdRuleSet inputRuleSet;
				if (reverseRuleSetDict.TryGetValue(hrs, out inputRuleSet))
				{
					// ensure the ruleset is processed before importing its members
					if (VisitRuleSet(inputRuleSet) != hrs)
						Debug.Fail("this shouldn't happen");
				}
				return hrs;
			}

			public object VisitRule(XshdRule rule)
			{
				return new HighlightingRule
				{
					Color = GetColor(rule, rule.ColorReference),
					Regex = CreateRegex(rule, rule.Regex, rule.RegexType)
				};
			}
		}
		#endregion

		static Exception Error(XshdElement element, string message)
		{
			if (element.LineNumber > 0)
				return new HighlightingDefinitionInvalidException(
					"Error at line " + element.LineNumber + ":\n" + message);
			else
				return new HighlightingDefinitionInvalidException(message);
		}

		Dictionary<string, HighlightingRuleSet> ruleSetDict = new Dictionary<string, HighlightingRuleSet>();
		Dictionary<string, HighlightingColor> colorDict = new Dictionary<string, HighlightingColor>();
		[OptionalField]
		Dictionary<string, string> propDict = new Dictionary<string, string>();

		private bool _isThemeInitialized;

		/// <summary>
		/// Defines highlighting theme information (if any is applicable) for this highlighting.
		/// </summary>
		private readonly SyntaxDefinition _themedHighlights;

		public HighlightingRuleSet MainRuleSet { get; private set; }

		public HighlightingRuleSet GetNamedRuleSet(string name)
		{
			ApplyTheme();

			if (string.IsNullOrEmpty(name))
				return MainRuleSet;

			HighlightingRuleSet r;
			if (ruleSetDict.TryGetValue(name, out r))
				return r;
			else
				return null;
		}

		public HighlightingColor GetNamedColor(string name)
		{
			ApplyTheme();

			HighlightingColor c;
			if (colorDict.TryGetValue(name, out c))
				return c;
			else
				return null;
		}

		public IEnumerable<HighlightingColor> NamedHighlightingColors
		{
			get
			{
				ApplyTheme();

				return colorDict.Values;
			}
		}

		public override string ToString()
		{
			return this.Name;
		}

		public IDictionary<string, string> Properties
		{
			get
			{
				return propDict;
			}
		}

		private void InitializeDefinitions(XshdSyntaxDefinition xshd, IHighlightingDefinitionReferenceResolver resolver)
		{
			this.Name = xshd.Name;

			// Create HighlightingRuleSet instances
			var rnev = new RegisterNamedElementsVisitor(this);
			xshd.AcceptElements(rnev);

			// Assign MainRuleSet so that references can be resolved
			foreach (XshdElement element in xshd.Elements)
			{
				XshdRuleSet xrs = element as XshdRuleSet;
				if (xrs != null && xrs.Name == null)
				{
					if (MainRuleSet != null)
						throw Error(element, "Duplicate main RuleSet. There must be only one nameless RuleSet!");
					else
						MainRuleSet = rnev.ruleSets[xrs];
				}
			}

			if (MainRuleSet == null)
				throw new HighlightingDefinitionInvalidException("Could not find main RuleSet.");

			// Translate elements within the rulesets (resolving references and processing imports)
			xshd.AcceptElements(new TranslateElementVisitor(this, rnev.ruleSets, resolver));

			foreach (var p in xshd.Elements.OfType<XshdProperty>())
				propDict.Add(p.Name, p.Value);
		}

		private void ApplyTheme()
		{
			if (_themedHighlights == null || _isThemeInitialized)
				return;

			_isThemeInitialized = true;

			// Replace matching colors in highlightingdefinition with colors from theme sytaxdefinition.
			var items = colorDict.ToArray();
			for (int i = 0; i < items.Length; i++)
			{
				HighlightingColor newColor = _themedHighlights.ColorGet(items[i].Key);

				if (newColor != null)
				{
					string key = items[i].Key;
					colorDict.Remove(key);
					colorDict.Add(key, newColor);
				}
			}
		}
	}
}
