import React from 'react'
import ReactDOM from 'react-dom/client'
import App from './App.tsx'
import VoicePage from './voice/VoicePage.tsx'
import './index.css'

const pathname = window.location.pathname.replace(/\/+$/, '') || '/'
const RootComponent = pathname === '/voice' ? VoicePage : App

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <RootComponent />
  </React.StrictMode>,
)
