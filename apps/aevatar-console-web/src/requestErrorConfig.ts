import { message, notification } from 'antd';

// Error display policy.
enum ErrorShowType {
  SILENT = 0,
  WARN_MESSAGE = 1,
  ERROR_MESSAGE = 2,
  NOTIFICATION = 3,
  REDIRECT = 9,
}
// Backend response envelope.
interface ResponseStructure {
  success: boolean;
  data: unknown;
  errorCode?: number;
  errorMessage?: string;
  showType?: ErrorShowType;
}

type BusinessErrorInfo = Omit<ResponseStructure, 'success'>;

type RequestErrorOptions = {
  skipErrorHandler?: boolean;
};

type RequestLikeError = Error & {
  name: string;
  info?: BusinessErrorInfo;
  response?: {
    status?: number;
  };
  request?: unknown;
};

/**
 * @name error handling
 * Request error handling hook used by the Umi request plugin.
 * @doc https://umijs.org/docs/max/request#config
 */
export const errorConfig = {
  // Umi request error handling.
  errorConfig: {
    // Throw business errors from the backend envelope.
    errorThrower: (res: ResponseStructure) => {
      const { success, data, errorCode, errorMessage, showType } = res;
      if (!success) {
        const error = new Error(errorMessage) as RequestLikeError;
        error.name = 'BizError';
        error.info = { errorCode, errorMessage, showType, data };
        throw error;
      }
    },
    // Receive and render errors.
    errorHandler: (error: RequestLikeError, opts: RequestErrorOptions) => {
      if (opts?.skipErrorHandler) throw error;
      // Errors thrown by errorThrower above.
      if (error.name === 'BizError') {
        const errorInfo = error.info;
        if (errorInfo) {
          const { errorMessage, errorCode } = errorInfo;
          switch (errorInfo.showType) {
            case ErrorShowType.SILENT:
              // do nothing
              break;
            case ErrorShowType.WARN_MESSAGE:
              message.warning(errorMessage);
              break;
            case ErrorShowType.ERROR_MESSAGE:
              message.error(errorMessage);
              break;
            case ErrorShowType.NOTIFICATION:
              notification.open({
                title: errorCode,
                description: errorMessage,
              });
              break;
            case ErrorShowType.REDIRECT:
              // TODO: redirect
              break;
            default:
              message.error(errorMessage);
          }
        }
      } else if (error.response) {
        // Axios received a non-2xx response.
        message.error(`Response status:${error.response.status}`);
      } else if (error.request) {
        // Request was sent but no response was received.
        message.error('None response! Please retry.');
      } else {
        // Request setup failed.
        message.error('Request error, please retry.');
      }
    },
  },

  requestInterceptors: [
    (config: Record<string, unknown>) => {
      return config;
    },
  ],
};
