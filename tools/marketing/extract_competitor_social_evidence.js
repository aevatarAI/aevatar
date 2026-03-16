#!/usr/bin/env node

const DEFAULT_SERVICE_BASE_URL = "http://127.0.0.1:3011";
const DEFAULT_MAX_URLS_PER_COMPETITOR = 4;

function readStdin() {
  return new Promise((resolve, reject) => {
    let data = "";
    process.stdin.setEncoding("utf8");
    process.stdin.on("data", (chunk) => {
      data += chunk;
    });
    process.stdin.on("end", () => resolve(data));
    process.stdin.on("error", reject);
  });
}

function tryParseJson(value) {
  if (value == null) return null;
  if (typeof value === "object") return value;
  const text = String(value).trim();
  if (!text) return null;
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

function normalizeText(value) {
  return String(value || "")
    .toLowerCase()
    .replace(/[`*_#[\](){}|<>]/g, " ")
    .replace(/https?:\/\/\S+/g, " ")
    .replace(/[^a-z0-9]+/g, " ")
    .replace(/\s+/g, " ")
    .trim();
}

function parseCompetitorShortlist(value) {
  const text = String(value || "");
  const lines = text.split(/\r?\n/);
  const names = [];

  for (const rawLine of lines) {
    const line = rawLine.trim();
    if (!line.startsWith("-")) continue;
    const body = line.replace(/^-+\s*/, "");
    const match = body.match(/^([A-Za-z0-9.&+\- ]{2,40}?)(?::|：|;|；|$)/);
    const candidate = (match ? match[1] : body).trim();
    if (!candidate || /direct competitors|engagement benchmark/i.test(candidate)) continue;
    if (!names.includes(candidate)) names.push(candidate);
  }

  return names;
}

function detectPlatform(url) {
  try {
    const hostname = new URL(String(url || "")).hostname.toLowerCase();
    if (hostname === "linkedin.com" || hostname.endsWith(".linkedin.com")) return "linkedin";
    if (hostname === "youtube.com" || hostname.endsWith(".youtube.com") || hostname === "youtu.be") return "youtube";
    if (hostname === "instagram.com" || hostname.endsWith(".instagram.com")) return "instagram";
    if (hostname === "tiktok.com" || hostname.endsWith(".tiktok.com")) return "tiktok";
    if (hostname === "x.com" || hostname.endsWith(".x.com") || hostname === "twitter.com" || hostname.endsWith(".twitter.com")) return "x";
  } catch {
    return null;
  }
  return null;
}

function isLikelySocialUrl(url) {
  return detectPlatform(url) !== null;
}

function isLikelyWebsiteUrl(url) {
  try {
    const parsed = new URL(String(url || ""));
    return /^https?:$/.test(parsed.protocol) && detectPlatform(parsed.toString()) === null;
  } catch {
    return false;
  }
}

function isLikelyProfileUrl(url, platform) {
  const lower = String(url || "").toLowerCase();
  if (platform === "linkedin") return /linkedin\.com\/(company|showcase)\//.test(lower);
  if (platform === "youtube") return /youtube\.com\/(@|channel\/|user\/|c\/)/.test(lower);
  if (platform === "instagram") return /instagram\.com\/(?!p\/|reel\/|stories\/)[^/?#]+\/?$/.test(lower);
  if (platform === "tiktok") return /tiktok\.com\/@[^/]+\/?$/.test(lower);
  if (platform === "x") return /(x|twitter)\.com\/(?!home|explore|search|status\/)[^/?#]+\/?$/.test(lower);
  return false;
}

function isLikelyPostUrl(url, platform) {
  const lower = String(url || "").toLowerCase();
  if (platform === "linkedin") return /linkedin\.com\/(posts\/|feed\/update\/|activity-)/.test(lower);
  if (platform === "youtube") return /youtube\.com\/(watch\?v=|shorts\/)/.test(lower);
  if (platform === "instagram") return /instagram\.com\/(p|reel)\//.test(lower);
  if (platform === "tiktok") return /tiktok\.com\/@[^/]+\/video\//.test(lower);
  if (platform === "x") return /(x|twitter)\.com\/[^/]+\/status\//.test(lower);
  return false;
}

function parseSearchResults(source) {
  const chunks = String(source || "")
    .split("\n---\n")
    .map((part) => part.trim())
    .filter(Boolean);

  const results = [];
  for (const chunk of chunks) {
    const parsed = tryParseJson(chunk);
    if (!parsed || !Array.isArray(parsed.results)) continue;
    results.push(
      ...parsed.results
        .map((item) => ({
          title: String(item?.title || "").trim(),
          url: String(item?.url || "").trim(),
          content: String(item?.content || "").trim(),
          score: typeof item?.score === "number" ? item.score : 0
        }))
        .filter((item) => item.url)
    );
  }

  return results;
}

function matchesCompetitor(result, competitorName) {
  const haystack = normalizeText([result.title, result.url, result.content].join(" "));
  const competitor = normalizeText(competitorName);
  if (!competitor) return false;
  if (haystack.includes(competitor)) return true;

  const tokens = competitor.split(" ").filter((token) => token.length >= 4);
  return tokens.length > 0 && tokens.every((token) => haystack.includes(token));
}

function scoreCandidate(result, competitorName) {
  const platform = detectPlatform(result.url);
  if (!platform) return -1;

  let score = 0;
  if (isLikelyProfileUrl(result.url, platform)) score += 6;
  if (isLikelyPostUrl(result.url, platform)) score += 3;
  if (matchesCompetitor(result, competitorName)) score += 4;
  if (/official|company|channel/i.test(result.title)) score += 1;
  if (result.score) score += Math.min(2, result.score);
  return score;
}

function looksLikeGenericComparison(result) {
  const haystack = normalizeText([result.title, result.url, result.content].join(" "));
  if (!haystack) return false;
  return [
    " vs ",
    "versus",
    "comparison",
    "alternative",
    "alternatives",
    "best ",
    "top ",
    "review",
    "reviews",
    "compare",
    "competitor",
    "competitors",
    "pricing comparison",
    "project management tools",
    "software roundup",
    "list of "
  ].some((token) => haystack.includes(token));
}

function extractedMatchesCompetitor(data, competitorName) {
  const competitor = normalizeText(competitorName);
  if (!competitor) return false;

  const haystack = normalizeText([
    data?.accountName || "",
    data?.handle || "",
    data?.profileUrl || "",
    data?.bioOrSummary || ""
  ].join(" "));

  if (!haystack) return false;
  if (haystack.includes(competitor)) return true;

  const tokens = competitor.split(" ").filter((token) => token.length >= 4);
  return tokens.length > 0 && tokens.every((token) => haystack.includes(token));
}

function uniqueBy(items, keySelector) {
  const seen = new Set();
  const result = [];
  for (const item of items) {
    const key = keySelector(item);
    if (!key || seen.has(key)) continue;
    seen.add(key);
    result.push(item);
  }
  return result;
}

function candidateDomainMatchesCompetitor(url, competitorName) {
  try {
    const hostname = new URL(url).hostname.replace(/^www\./, "").toLowerCase();
    const compactCompetitor = normalizeText(competitorName).replace(/\s+/g, "");
    if (!compactCompetitor) return false;
    if (hostname.includes(compactCompetitor)) return true;

    const tokens = normalizeText(competitorName)
      .split(" ")
      .filter((token) => token.length >= 4);
    return tokens.some((token) => hostname.includes(token));
  } catch {
    return false;
  }
}

function collectOfficialWebsiteCandidates(competitorName, generalResults, maxUrls) {
  const ranked = [];
  for (const result of generalResults) {
    if (!isLikelyWebsiteUrl(result.url)) continue;
    if (!matchesCompetitor(result, competitorName) && !candidateDomainMatchesCompetitor(result.url, competitorName)) {
      continue;
    }

    let score = 0;
    if (candidateDomainMatchesCompetitor(result.url, competitorName)) score += 5;
    if (matchesCompetitor(result, competitorName)) score += 3;
    if (/official|homepage|pricing|product|project management/i.test(result.title)) score += 1;
    if (result.score) score += Math.min(2, result.score);

    ranked.push({
      url: result.url,
      title: result.title,
      content: result.content,
      score
    });
  }

  return uniqueBy(
    ranked.sort((a, b) => b.score - a.score),
    (item) => item.url
  ).slice(0, maxUrls);
}

function collectCompetitorCandidates(competitorName, profileResults, activityResults, maxUrls) {
  const merged = [];
  for (const result of [...profileResults, ...activityResults]) {
    if (!isLikelySocialUrl(result.url)) continue;
    if (!matchesCompetitor(result, competitorName)) continue;
    const platform = detectPlatform(result.url);
    const fromProfileSearch = profileResults.includes(result);
    const profileLike = isLikelyProfileUrl(result.url, platform);
    const postLike = isLikelyPostUrl(result.url, platform);
    const genericComparison = looksLikeGenericComparison(result);

    if (fromProfileSearch && !profileLike) continue;
    if (!fromProfileSearch && genericComparison && !profileLike) continue;
    if (!profileLike && !postLike) continue;

    merged.push({
      competitorName,
      title: result.title,
      url: result.url,
      content: result.content,
      score: scoreCandidate(result, competitorName),
      platform,
      source: fromProfileSearch ? "profile_search" : "recent_activity_search"
    });
  }

  const ranked = uniqueBy(
    merged.sort((a, b) => b.score - a.score),
    (item) => item.url
  );

  const preferred = [];
  const profiles = ranked.filter((item) => isLikelyProfileUrl(item.url, item.platform));
  const posts = ranked.filter((item) => isLikelyPostUrl(item.url, item.platform));

  preferred.push(...profiles.slice(0, Math.max(1, Math.min(2, maxUrls))));
  for (const post of posts) {
    if (preferred.length >= maxUrls) break;
    if (!preferred.some((item) => item.url === post.url)) preferred.push(post);
  }
  for (const item of ranked) {
    if (preferred.length >= maxUrls) break;
    if (!preferred.some((candidate) => candidate.url === item.url)) preferred.push(item);
  }

  return preferred;
}

async function extractSocialEvidence(baseUrl, candidate) {
  const response = await fetch(`${baseUrl}/api/social/extract`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      url: candidate.url,
      platform: candidate.platform ?? "auto",
      mode: "auto",
      options: {
        maxPosts: isLikelyProfileUrl(candidate.url, candidate.platform) ? 4 : 2,
        includePostSamples: true
      }
    })
  });

  const text = await response.text();
  const parsed = tryParseJson(text);
  if (!response.ok) {
    return {
      success: false,
      url: candidate.url,
      platform: candidate.platform,
      source: candidate.source,
      error: parsed?.error?.message || text || `HTTP ${response.status}`
    };
  }

  return {
    success: true,
    url: candidate.url,
    platform: candidate.platform,
    source: candidate.source,
    response: parsed
  };
}

async function extractWebsiteSocialLinks(baseUrl, websiteUrl) {
  const response = await fetch(`${baseUrl}/api/url/extract`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      url: websiteUrl,
      selectors: {
        socialLinks:
          "a[href*='linkedin.com'], a[href*='youtube.com'], a[href*='youtu.be'], a[href*='instagram.com'], a[href*='tiktok.com'], a[href*='x.com'], a[href*='twitter.com'], a[href*='threads.net']"
      },
      options: {
        returnText: false,
        returnHtml: false,
        maxItemsPerSelector: 20
      }
    })
  });

  const text = await response.text();
  const parsed = tryParseJson(text);
  if (!response.ok || !parsed?.data) return [];

  const links = Array.isArray(parsed.data.socialLinks)
    ? parsed.data.socialLinks
    : parsed.data.socialLinks
      ? [parsed.data.socialLinks]
      : [];

  return uniqueBy(
    links
      .map((item) => {
        const href = item?.href || null;
        if (!href) return null;
        try {
          return new URL(href, websiteUrl).toString();
        } catch {
          return null;
        }
      })
      .filter((url) => url && isLikelySocialUrl(url))
      .map((url) => ({
        url,
        platform: detectPlatform(url),
        source: "official_website_social_link"
      })),
    (item) => item.url
  );
}

function summarizeCompetitorEvidence(name, candidates, extracted) {
  const successful = extracted.filter(
    (item) =>
      item.success &&
      item.response?.data &&
      extractedMatchesCompetitor(item.response.data, name)
  );
  const failed = extracted.filter((item) => !item.success);
  const confirmedPlatforms = [];
  const postingHints = [];
  const visibleFormats = new Set();
  const visibleHashtags = new Set();
  const visibleHooks = [];

  for (const item of successful) {
    const data = item.response.data;
    if (data?.platformPresence?.confirmed && data.platform) {
      confirmedPlatforms.push({
        platform: data.platform,
        profileUrl: data.profileUrl,
        evidence: data.platformPresence.evidence || null
      });
    }

    if (data?.postingFrequencyHint && data.postingFrequencyHint !== "unknown") {
      postingHints.push({
        platform: data.platform,
        hint: data.postingFrequencyHint,
        profileUrl: data.profileUrl
      });
    }

    for (const post of data?.recentPosts || []) {
      if (post?.format) visibleFormats.add(post.format);
      for (const hashtag of post?.hashtags || []) {
        if (hashtag && visibleHashtags.size < 12) visibleHashtags.add(hashtag);
      }
      if (post?.hook && visibleHooks.length < 8) visibleHooks.push(post.hook);
    }
  }

  return {
    competitor: name,
    candidateUrls: candidates,
    extractedProfiles: successful.map((item) => item.response.data),
    failures: failed,
    platformPresenceConfirmed: confirmedPlatforms,
    postingFrequencyHints: postingHints,
    visibleFormats: Array.from(visibleFormats),
    visibleHashtags: Array.from(visibleHashtags),
    visibleHooks
  };
}

async function main() {
  const raw = await readStdin();
  const payload = tryParseJson(raw) || {};
  const serviceBaseUrl = String(payload.serviceBaseUrl || DEFAULT_SERVICE_BASE_URL).replace(/\/$/, "");
  const competitorShortlist = parseCompetitorShortlist(payload.competitorShortlist);
  const competitorSearchResults = parseSearchResults(payload.competitorSearchResults);
  const profileResults = parseSearchResults(payload.socialProfilesSearch);
  const activityResults = parseSearchResults(payload.recentActivitySearch);
  const maxUrlsPerCompetitor = Number(payload.maxUrlsPerCompetitor || DEFAULT_MAX_URLS_PER_COMPETITOR);

  const competitors = [];

  for (const competitorName of competitorShortlist) {
    const searchCandidates = collectCompetitorCandidates(
      competitorName,
      profileResults,
      activityResults,
      maxUrlsPerCompetitor
    );
    const officialWebsiteCandidates = collectOfficialWebsiteCandidates(
      competitorName,
      competitorSearchResults,
      2
    );
    const websiteSocialCandidates = [];
    for (const website of officialWebsiteCandidates) {
      // eslint-disable-next-line no-await-in-loop
      const links = await extractWebsiteSocialLinks(serviceBaseUrl, website.url);
      websiteSocialCandidates.push(...links);
    }
    const candidates = uniqueBy(
      [...websiteSocialCandidates, ...searchCandidates],
      (item) => item.url
    ).slice(0, maxUrlsPerCompetitor);

    const extracted = [];
    for (const candidate of candidates) {
      // eslint-disable-next-line no-await-in-loop
      extracted.push(await extractSocialEvidence(serviceBaseUrl, candidate));
    }

    competitors.push({
      ...summarizeCompetitorEvidence(competitorName, candidates, extracted),
      officialWebsiteCandidates
    });
  }

  process.stdout.write(JSON.stringify({
    serviceBaseUrl,
    competitorCount: competitors.length,
    competitors
  }));
}

main().catch((error) => {
  process.stderr.write(`${error.message || String(error)}\n`);
  process.exitCode = 1;
});
