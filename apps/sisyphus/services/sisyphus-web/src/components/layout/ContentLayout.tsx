import { Outlet } from 'react-router-dom'

/** Fixed-width centered layout for all pages except Graph */
export default function ContentLayout() {
  return (
    <div className="h-full flex justify-center overflow-hidden">
      <div className="w-[85%] h-full flex flex-col">
        <Outlet />
      </div>
    </div>
  )
}
