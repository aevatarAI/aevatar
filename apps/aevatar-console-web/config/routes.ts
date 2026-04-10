/**
 * @name umi 的路由配置
 * @description Aevatar Console 当前同时使用 path/component/routes/redirect/name/icon，以及用于菜单组织的 hideInMenu、parentKeys 和未来 badge 注入预留字段。
 * @param path  path 只支持两种占位符配置，第一种是动态参数 :id 的形式，第二种是 * 通配符，通配符只能出现路由字符串的最后。
 * @param component 配置 location 和 path 匹配后用于渲染的 React 组件路径。可以是绝对路径，也可以是相对路径，如果是相对路径，会从 src/pages 开始找起。
 * @param routes 配置子路由，通常在需要为多个路径增加 layout 组件时使用。
 * @param redirect 配置路由跳转
 * @param wrappers 配置路由组件的包装组件，通过包装组件可以为当前的路由组件组合进更多的功能。 比如，可以用于路由级别的权限校验
 * @param name 配置路由的标题，默认读取国际化文件 menu.ts 中 menu.xxxx 的值，如配置 name 为 login，则读取 menu.ts 中 menu.login 的取值作为标题
 * @param icon 配置路由的图标，取值参考 https://ant.design/components/icon-cn， 注意去除风格后缀和大小写，如想要配置图标为 <StepBackwardOutlined /> 则取值应为 stepBackward 或 StepBackward，如想要配置图标为 <UserOutlined /> 则取值应为 user 或者 User
 * @doc https://umijs.org/docs/guides/routes
 */
export default [
  {
    path: "/login",
    component: "./login",
    layout: false,
  },
  {
    path: "/auth/callback",
    component: "./auth/callback",
    layout: false,
  },
  {
    path: "/overview",
    redirect: "/teams",
    hideInMenu: true,
  },
  {
    path: "/teams",
    name: "我的团队",
    component: "./scopes/overview",
    menuGroupKey: "teams",
  },
  {
    path: "/teams/new",
    name: "组建团队",
    component: "./teams/new",
    menuGroupKey: "teams",
  },
  {
    path: "/teams/:scopeId",
    name: "团队详情",
    component: "./teams",
    hideInMenu: true,
    parentKeys: ["/teams"],
  },
  {
    path: "/scopes/assets",
    component: "./scopes/assets",
    hideInMenu: true,
  },
  {
    path: "/studio",
    component: "./studio",
    hideInMenu: true,
  },
  {
    path: "/runtime/workflows",
    component: "./workflows",
    hideInMenu: true,
  },
  {
    path: "/runtime/primitives",
    name: "连接器",
    component: "./primitives",
    hideInMenu: true,
  },
  {
    path: "/chat",
    component: "./chat",
    hideInMenu: true,
  },
  {
    path: "/scopes/invoke",
    component: "./scopes/invoke",
    hideInMenu: true,
  },
  {
    path: "/runtime/runs",
    name: "事件流",
    component: "./runs",
    hideInMenu: true,
  },
  {
    path: "/runtime/mission-control",
    name: "Mission Control",
    component: "./MissionControl",
    hideInMenu: true,
  },
  {
    path: "/runtime/explorer",
    name: "Topology",
    component: "./actors",
    menuGroupKey: "platform",
  },
  {
    path: "/runtime/gagents",
    name: "成员",
    component: "./gagents",
    hideInMenu: true,
  },
  {
    path: "/services",
    name: "Services",
    component: "./services",
    menuGroupKey: "platform",
  },
  {
    path: "/services/:serviceId",
    component: "./services",
    hideInMenu: true,
    parentKeys: ["/services"],
  },
  {
    path: "/deployments",
    name: "Deployments",
    component: "./Deployments",
    menuGroupKey: "platform",
  },
  {
    path: "/governance",
    name: "Governance",
    component: "./governance",
    menuGroupKey: "platform",
  },
  {
    path: "/governance/policies",
    component: "./governance/policies",
    hideInMenu: true,
    parentKeys: ["/governance"],
  },
  {
    path: "/governance/bindings",
    component: "./governance/bindings",
    hideInMenu: true,
    parentKeys: ["/governance"],
  },
  {
    path: "/governance/endpoints",
    component: "./governance/endpoints",
    hideInMenu: true,
    parentKeys: ["/governance"],
  },
  {
    path: "/governance/activation",
    component: "./governance/activation",
    hideInMenu: true,
    parentKeys: ["/governance"],
  },
  {
    path: "/scopes/overview",
    redirect: "/teams",
    hideInMenu: true,
  },
  {
    path: "/settings",
    name: "设置",
    component: "./settings/account",
    menuGroupKey: "settings",
  },
  {
    path: "/scopes",
    redirect: "/teams",
    hideInMenu: true,
  },
  {
    path: "/scopes/workflows",
    redirect: "/runtime/workflows",
    hideInMenu: true,
  },
  {
    path: "/scopes/scripts",
    redirect: "/studio?tab=scripts",
    hideInMenu: true,
  },
  {
    path: "/governance/audit",
    redirect: "/governance",
    hideInMenu: true,
  },
  {
    path: "/workflows",
    redirect: "/runtime/workflows",
    hideInMenu: true,
  },
  {
    path: "/primitives",
    redirect: "/runtime/primitives",
    hideInMenu: true,
  },
  {
    path: "/runs",
    redirect: "/runtime/runs",
    hideInMenu: true,
  },
  {
    path: "/actors",
    redirect: "/runtime/explorer",
    hideInMenu: true,
  },
  {
    path: "/gagents",
    redirect: "/runtime/gagents",
    hideInMenu: true,
  },
  {
    path: "/mission-control",
    redirect: "/runtime/mission-control",
    hideInMenu: true,
  },
  {
    path: "/",
    redirect: "/teams",
  },
  {
    component: "404",
    layout: false,
    path: "/*",
  },
];
