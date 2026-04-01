import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import { NyxIDAppProvider } from './auth/NyxIDProvider'
import App from './App'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <NyxIDAppProvider>
      <App />
    </NyxIDAppProvider>
  </StrictMode>,
)
