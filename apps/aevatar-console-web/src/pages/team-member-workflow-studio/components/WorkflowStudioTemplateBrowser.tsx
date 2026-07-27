import {
  AppstoreOutlined,
  ArrowRightOutlined,
  CheckCircleOutlined,
  CloseOutlined,
  SearchOutlined,
} from "@ant-design/icons";
import { useQuery } from "@tanstack/react-query";
import { useIntl } from "@umijs/max";
import {
  Alert,
  Button,
  Empty,
  Input,
  Select,
  Skeleton,
  Space,
  Spin,
  Tag,
  Typography,
} from "antd";
import React from "react";
import GraphCanvas from "@/shared/graphs/GraphCanvas";
import { normalizeConsoleLocale } from "@/shared/i18n/localeProvider";
import { t } from "@/shared/i18n/messages";
import { studioApi } from "@/shared/studio/api";
import {
  buildStudioGraphElements,
  formatStudioStepTypeLabel,
  STUDIO_GRAPH_CATEGORIES,
} from "@/shared/studio/graph";
import type {
  StudioWorkflowTemplateDetail,
  StudioWorkflowTemplateLocalizedText,
  StudioWorkflowTemplateSummary,
} from "@/shared/studio/models";

const TEMPLATE_PAGE_SIZE = 100;
const TEMPLATE_STEP_TYPES = STUDIO_GRAPH_CATEGORIES.flatMap(
  (category) => category.items,
);

type WorkflowStudioTemplateBrowserProps = {
  readonly actionDisabled?: boolean;
  readonly actionLabel?: string;
  readonly actionPending?: boolean;
  readonly onClose: () => void;
  readonly onSelectTemplate: (templateId: string) => void;
  readonly onUseTemplate?: (detail: StudioWorkflowTemplateDetail) => Promise<void> | void;
  readonly open: boolean;
  readonly selectedTemplateId: string;
};

const templateBrowserCss = `
.workflow-template-browser {
  background: rgba(15, 23, 42, 0.28);
  inset: 0;
  padding: 24px;
  position: absolute;
  z-index: 32;
}

.workflow-template-browser__surface {
  background: #ffffff;
  border: 1px solid #dbe3ee;
  border-radius: 8px;
  box-shadow: 0 24px 64px rgba(15, 23, 42, 0.20);
  display: grid;
  grid-template-columns: minmax(250px, 300px) minmax(0, 1fr);
  height: 100%;
  margin: 0 auto;
  max-width: 1180px;
  min-height: 0;
  overflow: hidden;
}

.workflow-template-browser__catalog,
.workflow-template-browser__detail {
  display: flex;
  flex-direction: column;
  min-height: 0;
  min-width: 0;
}

.workflow-template-browser__catalog {
  background: #f8fafc;
  border-right: 1px solid #e2e8f0;
}

.workflow-template-browser__header {
  align-items: flex-start;
  border-bottom: 1px solid #e2e8f0;
  display: flex;
  gap: 12px;
  justify-content: space-between;
  min-height: 72px;
  padding: 16px 18px;
}

.workflow-template-browser__filters {
  display: grid;
  gap: 10px;
  padding: 14px 16px 12px;
}

.workflow-template-browser__results {
  display: grid;
  gap: 8px;
  min-height: 180px;
  overflow: auto;
  padding: 0 12px 16px;
}

.workflow-template-browser__result {
  background: #ffffff;
  border: 1px solid #dbe3ee;
  border-radius: 7px;
  color: #0f172a;
  cursor: pointer;
  display: grid;
  gap: 7px;
  min-height: 112px;
  padding: 12px;
  text-align: left;
  width: 100%;
}

.workflow-template-browser__result:hover:not(:disabled),
.workflow-template-browser__result:focus-visible {
  border-color: #2563eb;
  box-shadow: 0 0 0 2px rgba(37, 99, 235, 0.14);
  outline: none;
}

.workflow-template-browser__result[aria-pressed="true"] {
  background: #eff6ff;
  border-color: #2563eb;
}

.workflow-template-browser__result:disabled {
  background: #f1f5f9;
  color: #64748b;
  cursor: not-allowed;
}

.workflow-template-browser__detail-body {
  display: grid;
  gap: 18px;
  min-height: 0;
  overflow: auto;
  padding: 20px;
}

.workflow-template-browser__preview {
  background: #f7f9fc;
  border: 1px solid #dbe3ee;
  border-radius: 8px;
  height: clamp(260px, 40vh, 430px);
  min-height: 260px;
  overflow: hidden;
}

.workflow-template-browser__facts {
  display: grid;
  gap: 12px;
  grid-template-columns: repeat(2, minmax(0, 1fr));
}

.workflow-template-browser__fact {
  border-left: 3px solid #cbd5e1;
  display: grid;
  gap: 4px;
  min-width: 0;
  padding-left: 10px;
}

.workflow-template-browser__footer {
  align-items: center;
  background: #ffffff;
  border-top: 1px solid #e2e8f0;
  display: flex;
  gap: 12px;
  justify-content: flex-end;
  min-height: 64px;
  padding: 12px 20px;
}

@media (max-width: 820px) {
  .workflow-template-browser {
    padding: 0;
  }

  .workflow-template-browser__surface {
    border: 0;
    border-radius: 0;
    grid-template-columns: minmax(210px, 34%) minmax(0, 1fr);
    max-width: none;
  }

  .workflow-template-browser__detail-body {
    padding: 16px;
  }
}

@media (max-width: 560px) {
  .workflow-template-browser__surface {
    grid-template-columns: minmax(0, 1fr);
    grid-template-rows: minmax(220px, 38%) minmax(0, 1fr);
  }

  .workflow-template-browser__catalog {
    border-bottom: 1px solid #e2e8f0;
    border-right: 0;
  }

  .workflow-template-browser__header {
    min-height: 62px;
    padding: 12px 14px;
  }

  .workflow-template-browser__filters {
    grid-template-columns: minmax(0, 1fr) minmax(120px, 38%);
    padding: 10px 12px;
  }

  .workflow-template-browser__results {
    display: flex;
    min-height: 96px;
    overflow-x: auto;
    overflow-y: hidden;
    padding: 0 12px 12px;
  }

  .workflow-template-browser__result {
    flex: 0 0 min(280px, 78vw);
    min-height: 96px;
  }

  .workflow-template-browser__facts {
    grid-template-columns: minmax(0, 1fr);
  }

  .workflow-template-browser__preview {
    height: 280px;
  }
}

@media (prefers-reduced-motion: reduce) {
  .workflow-template-browser *,
  .workflow-template-browser *::before,
  .workflow-template-browser *::after {
    scroll-behavior: auto !important;
    transition-duration: 0.01ms !important;
  }
}
`;

function localize(
  value: StudioWorkflowTemplateLocalizedText,
  locale: "en-US" | "zh-CN",
): string {
  return value[locale] || value["en-US"];
}

function formatCompatibilityReason(
  reason: StudioWorkflowTemplateSummary["compatibility"]["reason"],
): string {
  switch (reason) {
    case "WorkflowSchemaUnsupported":
      return t(
        "teamMemberWorkflowStudio.templates.unsupportedSchema",
        "Workflow schema unsupported",
      );
    case "RequiredPrimitiveUnavailable":
      return t(
        "teamMemberWorkflowStudio.templates.unavailablePrimitive",
        "Required primitive unavailable",
      );
    default:
      return t(
        "teamMemberWorkflowStudio.templates.compatible",
        "Compatible",
      );
  }
}

function describeCatalogFailure(error: unknown): string {
  if (typeof navigator !== "undefined" && navigator.onLine === false) {
    return t(
      "teamMemberWorkflowStudio.templates.offline",
      "You appear to be offline. Reconnect and retry the template catalog.",
    );
  }
  if (error instanceof DOMException && error.name === "AbortError") {
    return t(
      "teamMemberWorkflowStudio.templates.timeout",
      "The template catalog request timed out. Retry when the connection is stable.",
    );
  }
  return t(
    "teamMemberWorkflowStudio.templates.loadFailed",
    "The template catalog could not be loaded.",
  );
}

const WorkflowStudioTemplateBrowser: React.FC<
  WorkflowStudioTemplateBrowserProps
> = ({
  actionDisabled = true,
  actionLabel,
  actionPending = false,
  onClose,
  onSelectTemplate,
  onUseTemplate,
  open,
  selectedTemplateId,
}) => {
  const intl = useIntl();
  const locale = normalizeConsoleLocale(intl.locale);
  const headingRef = React.useRef<HTMLHeadingElement | null>(null);
  const [query, setQuery] = React.useState("");
  const [category, setCategory] = React.useState("");
  const [knownCategories, setKnownCategories] = React.useState<string[]>([]);
  const normalizedQuery = query.trim();

  const catalogQuery = useQuery({
    enabled: open,
    queryKey: ["workflow-templates", normalizedQuery, category],
    queryFn: ({ signal }) =>
      studioApi.listWorkflowTemplates({
        category,
        pageSize: TEMPLATE_PAGE_SIZE,
        query: normalizedQuery,
        signal,
      }),
    retry: false,
  });

  React.useEffect(() => {
    const categories = catalogQuery.data?.items
      .map((item) => item.category.trim())
      .filter(Boolean);
    if (!categories?.length) {
      return;
    }
    setKnownCategories((current) =>
      Array.from(new Set([...current, ...categories])).sort((left, right) =>
        left.localeCompare(right),
      ),
    );
  }, [catalogQuery.data?.items]);

  React.useEffect(() => {
    if (open) {
      headingRef.current?.focus();
    }
  }, [open]);

  const selectedSummary = React.useMemo(
    () =>
      catalogQuery.data?.items.find(
        (item) => item.templateId === selectedTemplateId,
      ) ?? null,
    [catalogQuery.data?.items, selectedTemplateId],
  );
  const detailQuery = useQuery({
    enabled:
      open &&
      Boolean(selectedSummary) &&
      selectedSummary?.compatibility.status === "Compatible",
    queryKey: [
      "workflow-template-detail",
      selectedSummary?.templateId,
      selectedSummary?.revision,
    ],
    queryFn: ({ signal }) =>
      studioApi.getWorkflowTemplate(
        selectedSummary?.templateId ?? "",
        selectedSummary?.revision ?? "",
        signal,
      ),
    retry: false,
  });
  const parseQuery = useQuery({
    enabled:
      open &&
      Boolean(detailQuery.data) &&
      detailQuery.data?.compatibility.status === "Compatible",
    queryKey: [
      "workflow-template-preview",
      detailQuery.data?.templateId,
      detailQuery.data?.revision,
    ],
    queryFn: () =>
      studioApi.parseYaml({
        availableStepTypes: TEMPLATE_STEP_TYPES,
        yaml: detailQuery.data?.workflowYaml ?? "",
      }),
    retry: false,
  });

  if (!open) {
    return null;
  }

  const detail = detailQuery.data ?? null;
  const parsedDocument = parseQuery.data?.document ?? null;
  const graph = buildStudioGraphElements(parsedDocument);
  const blockingFinding = parseQuery.data?.findings?.find(
    (finding) => String(finding.level).trim().toLowerCase() === "error",
  );
  const detailUnavailable =
    detailQuery.isError || parseQuery.isError || Boolean(blockingFinding);
  const selectedIsIncompatible =
    selectedSummary?.compatibility.status === "Incompatible";
  const selectedNotFound =
    Boolean(selectedTemplateId) &&
    catalogQuery.isSuccess &&
    !selectedSummary;
  const useTemplateDisabled =
    actionDisabled ||
    actionPending ||
    !detail ||
    detailUnavailable ||
    selectedIsIncompatible;
  const graphSummary = t(
    "teamMemberWorkflowStudio.templates.graphSummary",
    "{roles} {roleLabel}, {steps} {stepLabel}, {edges} {edgeLabel}",
    {
      roles: graph.roles.length,
      roleLabel:
        graph.roles.length === 1
          ? t("teamMemberWorkflowStudio.templates.roleSingular", "role")
          : t("teamMemberWorkflowStudio.templates.rolePlural", "roles"),
      steps: graph.steps.length,
      stepLabel:
        graph.steps.length === 1
          ? t("teamMemberWorkflowStudio.templates.stepSingular", "step")
          : t("teamMemberWorkflowStudio.templates.stepPlural", "steps"),
      edges: graph.edges.length,
      edgeLabel:
        graph.edges.length === 1
          ? t("teamMemberWorkflowStudio.templates.edgeSingular", "edge")
          : t("teamMemberWorkflowStudio.templates.edgePlural", "edges"),
    },
  );

  return (
    <div className="workflow-template-browser">
      <style>{templateBrowserCss}</style>
      <section
        aria-label={t(
          "teamMemberWorkflowStudio.templates.browserAria",
          "Workflow template browser",
        )}
        className="workflow-template-browser__surface"
      >
        <aside className="workflow-template-browser__catalog">
          <header className="workflow-template-browser__header">
            <div style={{ display: "grid", gap: 4, minWidth: 0 }}>
              <Typography.Title
                level={2}
                ref={headingRef}
                style={{ fontSize: 18, lineHeight: 1.3, margin: 0 }}
                tabIndex={-1}
              >
                {t(
                  "teamMemberWorkflowStudio.templates.title",
                  "Workflow templates",
                )}
              </Typography.Title>
              <Typography.Text style={{ color: "#64748b", fontSize: 12 }}>
                {t(
                  "teamMemberWorkflowStudio.templates.subtitle",
                  "Choose a complete workflow starting point.",
                )}
              </Typography.Text>
            </div>
            <Button
              aria-label={t(
                "teamMemberWorkflowStudio.templates.closeAria",
                "Close template browser",
              )}
              icon={<CloseOutlined />}
              onClick={onClose}
              style={{ height: 32, width: 32 }}
              type="text"
            />
          </header>
          <div className="workflow-template-browser__filters">
            <Input
              allowClear
              aria-label={t(
                "teamMemberWorkflowStudio.templates.searchAria",
                "Search workflow templates",
              )}
              onChange={(event) => {
                setQuery(event.target.value);
                if (selectedTemplateId) {
                  onSelectTemplate("");
                }
              }}
              placeholder={t(
                "teamMemberWorkflowStudio.templates.searchPlaceholder",
                "Search templates",
              )}
              prefix={<SearchOutlined />}
              role="searchbox"
              value={query}
            />
            <Select
              aria-label={t(
                "teamMemberWorkflowStudio.templates.categoryAria",
                "Filter templates by category",
              )}
              onChange={(value) => {
                setCategory(value);
                if (selectedTemplateId) {
                  onSelectTemplate("");
                }
              }}
              options={[
                {
                  label: t(
                    "teamMemberWorkflowStudio.templates.allCategories",
                    "All categories",
                  ),
                  value: "",
                },
                ...knownCategories.map((item) => ({ label: item, value: item })),
              ]}
              value={category}
            />
          </div>
          <div
            aria-live="polite"
            aria-relevant="additions text"
            className="workflow-template-browser__results"
          >
            {catalogQuery.isPending ? (
              <div
                aria-label={t(
                  "teamMemberWorkflowStudio.templates.loading",
                  "Loading workflow templates",
                )}
                role="status"
                style={{ minHeight: 180, padding: 4 }}
              >
                <Skeleton active paragraph={{ rows: 4 }} />
              </div>
            ) : catalogQuery.isError ? (
              <Alert
                description={
                  <Space orientation="vertical" size={10}>
                    <span>{describeCatalogFailure(catalogQuery.error)}</span>
                    <Button
                      onClick={() => void catalogQuery.refetch()}
                      size="small"
                    >
                      {t(
                        "teamMemberWorkflowStudio.templates.retry",
                        "Retry templates",
                      )}
                    </Button>
                  </Space>
                }
                title={t(
                  "teamMemberWorkflowStudio.templates.catalogUnavailable",
                  "Catalog unavailable",
                )}
                showIcon
                type="error"
              />
            ) : catalogQuery.data.items.length === 0 ? (
              <Empty
                description={t(
                  "teamMemberWorkflowStudio.templates.empty",
                  "No templates match these filters.",
                )}
                image={Empty.PRESENTED_IMAGE_SIMPLE}
              />
            ) : (
              catalogQuery.data.items.map((item) => {
                const title = localize(item.title, locale);
                const incompatible =
                  item.compatibility.status === "Incompatible";
                return (
                  <button
                    aria-label={t(
                      "teamMemberWorkflowStudio.templates.viewAria",
                      "View {title}",
                      { title },
                    )}
                    aria-pressed={selectedTemplateId === item.templateId}
                    className="workflow-template-browser__result"
                    disabled={incompatible}
                    key={`${item.templateId}:${item.revision}`}
                    onClick={() => onSelectTemplate(item.templateId)}
                    type="button"
                  >
                    <span
                      style={{
                        alignItems: "center",
                        display: "flex",
                        gap: 8,
                        justifyContent: "space-between",
                      }}
                    >
                      <Typography.Text strong>{title}</Typography.Text>
                      <ArrowRightOutlined aria-hidden />
                    </span>
                    <Typography.Text
                      style={{ color: "#64748b", fontSize: 12, lineHeight: 1.45 }}
                    >
                      {localize(item.summary, locale)}
                    </Typography.Text>
                    <Space size={6} wrap>
                      <Tag>{item.category}</Tag>
                      {incompatible ? (
                        <Tag color="error">
                          {formatCompatibilityReason(item.compatibility.reason)}
                        </Tag>
                      ) : null}
                    </Space>
                  </button>
                );
              })
            )}
          </div>
        </aside>

        <article className="workflow-template-browser__detail">
          {selectedNotFound ? (
            <div className="workflow-template-browser__detail-body">
              <Alert
                action={
                  <Button onClick={() => onSelectTemplate("")}>
                    {t(
                      "teamMemberWorkflowStudio.templates.backToCatalog",
                      "Back to catalog",
                    )}
                  </Button>
                }
                description={t(
                  "teamMemberWorkflowStudio.templates.notFoundDescription",
                  "This template is unavailable or no longer published.",
                )}
                title={t(
                  "teamMemberWorkflowStudio.templates.notFound",
                  "Template not found",
                )}
                showIcon
                type="warning"
              />
            </div>
          ) : !selectedSummary ? (
            <div
              className="workflow-template-browser__detail-body"
              style={{ alignContent: "center", justifyItems: "center" }}
            >
              <Empty
                description={t(
                  "teamMemberWorkflowStudio.templates.selectPrompt",
                  "Select a template to inspect its workflow graph.",
                )}
                image={<AppstoreOutlined style={{ color: "#94a3b8", fontSize: 44 }} />}
              />
            </div>
          ) : selectedIsIncompatible ? (
            <div className="workflow-template-browser__detail-body">
              <Alert
                description={t(
                  "teamMemberWorkflowStudio.templates.incompatibleDescription",
                  "This template cannot be used with the current Studio schema or primitive catalog.",
                )}
                title={formatCompatibilityReason(
                  selectedSummary.compatibility.reason,
                )}
                showIcon
                type="warning"
              />
            </div>
          ) : detailQuery.isPending || parseQuery.isPending ? (
            <div
              aria-live="polite"
              className="workflow-template-browser__detail-body"
              style={{ minHeight: 420 }}
            >
              <Space>
                <Spin size="small" />
                <Typography.Text>
                  {t(
                    "teamMemberWorkflowStudio.templates.detailLoading",
                    "Loading template detail and graph preview...",
                  )}
                </Typography.Text>
              </Space>
              <Skeleton active paragraph={{ rows: 8 }} />
            </div>
          ) : detailUnavailable || !detail || !parsedDocument ? (
            <div className="workflow-template-browser__detail-body">
              <Alert
                action={
                  <Button
                    onClick={() => {
                      void detailQuery.refetch();
                      void parseQuery.refetch();
                    }}
                  >
                    {t(
                      "teamMemberWorkflowStudio.templates.retryDetail",
                      "Retry detail",
                    )}
                  </Button>
                }
                description={
                  blockingFinding?.message ||
                  t(
                    "teamMemberWorkflowStudio.templates.previewUnavailableDescription",
                    "The current draft is unchanged. Retry or choose another template.",
                  )
                }
                title={t(
                  "teamMemberWorkflowStudio.templates.previewUnavailable",
                  "Template preview unavailable",
                )}
                showIcon
                type="error"
              />
            </div>
          ) : (
            <>
              <div className="workflow-template-browser__detail-body">
                <header style={{ display: "grid", gap: 8 }}>
                  <Space size={8} wrap>
                    <Tag color="blue">{detail.category}</Tag>
                    <Tag>{t("teamMemberWorkflowStudio.templates.revision", "Revision {revision}", { revision: detail.revision })}</Tag>
                    <Tag color="success" icon={<CheckCircleOutlined />}>
                      {t(
                        "teamMemberWorkflowStudio.templates.compatible",
                        "Compatible",
                      )}
                    </Tag>
                  </Space>
                  <Typography.Title
                    level={3}
                    style={{ fontSize: 22, lineHeight: 1.3, margin: 0 }}
                  >
                    {localize(detail.title, locale)}
                  </Typography.Title>
                  <Typography.Paragraph style={{ color: "#475569", margin: 0 }}>
                    {localize(detail.description, locale)}
                  </Typography.Paragraph>
                </header>

                <section
                  aria-label={t(
                    "teamMemberWorkflowStudio.templates.expectedIO",
                    "Expected input and output",
                  )}
                  className="workflow-template-browser__facts"
                >
                  <div className="workflow-template-browser__fact">
                    <Typography.Text strong>
                      {t(
                        "teamMemberWorkflowStudio.templates.expectedInput",
                        "Expected input",
                      )}
                    </Typography.Text>
                    <Typography.Text>
                      {localize(detail.expectedIO.input, locale)}
                    </Typography.Text>
                  </div>
                  <div className="workflow-template-browser__fact">
                    <Typography.Text strong>
                      {t(
                        "teamMemberWorkflowStudio.templates.expectedOutput",
                        "Expected output",
                      )}
                    </Typography.Text>
                    <Typography.Text>
                      {localize(detail.expectedIO.output, locale)}
                    </Typography.Text>
                  </div>
                </section>

                <section
                  aria-label={t(
                    "teamMemberWorkflowStudio.templates.requirements",
                    "Template requirements",
                  )}
                  style={{ display: "grid", gap: 8 }}
                >
                  <Typography.Text strong>
                    {t(
                      "teamMemberWorkflowStudio.templates.requirements",
                      "Template requirements",
                    )}
                  </Typography.Text>
                  <Space size={6} wrap>
                    <Tag>
                      {t(
                        "teamMemberWorkflowStudio.templates.schema",
                        "Schema {version}",
                        { version: detail.requirements.workflowSchemaVersion },
                      )}
                    </Tag>
                    {detail.requirements.requiresDefaultLLMRoute ? (
                      <Tag>
                        {t(
                          "teamMemberWorkflowStudio.templates.defaultLLMRoute",
                          "Default LLM route",
                        )}
                      </Tag>
                    ) : null}
                    {detail.requirements.requiresHumanInteraction ? (
                      <Tag>
                        {t(
                          "teamMemberWorkflowStudio.templates.humanInteraction",
                          "Human interaction",
                        )}
                      </Tag>
                    ) : null}
                    {detail.requirements.requiredPrimitives.map((primitive) => (
                      <Tag key={primitive}>{formatStudioStepTypeLabel(primitive)}</Tag>
                    ))}
                  </Space>
                </section>

                <section
                  aria-label={t(
                    "teamMemberWorkflowStudio.templates.graphPreview",
                    "Workflow graph preview",
                  )}
                  style={{ display: "grid", gap: 8 }}
                >
                  <Typography.Text strong>{graphSummary}</Typography.Text>
                  <div className="workflow-template-browser__preview" data-testid="template-graph-preview">
                    <GraphCanvas
                      autoFitKey={`${detail.templateId}:${detail.revision}`}
                      edges={graph.edges}
                      nodes={graph.nodes}
                      variant="studio"
                    />
                  </div>
                  <ul
                    aria-label={t(
                      "teamMemberWorkflowStudio.templates.graphTextSummary",
                      "Workflow graph text summary",
                    )}
                    style={{ display: "grid", gap: 4, listStyle: "none", margin: 0, padding: 0 }}
                  >
                    {graph.roles.map((role) => (
                      <li key={role.id}>
                        <Typography.Text>
                          {t(
                            "teamMemberWorkflowStudio.templates.roleSummary",
                            "Role {role} supports this workflow.",
                            { role: role.name || role.id },
                          )}
                        </Typography.Text>
                      </li>
                    ))}
                    {graph.steps.map((step) => (
                      <li key={step.id}>
                        <Typography.Text>
                          {t(
                            "teamMemberWorkflowStudio.templates.stepSummary",
                            "Step {step} uses {type}.",
                            {
                              step: step.id,
                              type: formatStudioStepTypeLabel(step.type),
                            },
                          )}
                        </Typography.Text>
                      </li>
                    ))}
                    {graph.edges.map((edge) => (
                      <li key={edge.id}>
                        <Typography.Text>
                          {t(
                            "teamMemberWorkflowStudio.templates.edgeSummary",
                            "Edge connects {source} to {target}.",
                            { source: edge.source, target: edge.target },
                          )}
                        </Typography.Text>
                      </li>
                    ))}
                  </ul>
                </section>
              </div>
              <footer className="workflow-template-browser__footer" aria-live="polite">
                <Button onClick={onClose}>
                  {t("teamMemberWorkflowStudio.templates.cancel", "Cancel")}
                </Button>
                <Button
                  disabled={useTemplateDisabled}
                  loading={actionPending}
                  onClick={() => {
                    if (detail && onUseTemplate) {
                      void onUseTemplate(detail);
                    }
                  }}
                  type="primary"
                >
                  {actionLabel ||
                    t(
                      "teamMemberWorkflowStudio.templates.useTemplate",
                      "Use template",
                    )}
                </Button>
              </footer>
            </>
          )}
        </article>
      </section>
    </div>
  );
};

export default WorkflowStudioTemplateBrowser;
